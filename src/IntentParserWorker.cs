using System.Text.Json;
using System.Threading.Channels;
using System.Globalization;
using BackgroundAssistant.Services;
using BackgroundAssistant.Tools;
using BackgroundAssistant.Memory;
using BackgroundAssistant.PluginRuntime;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundAssistant;

/// <summary>
/// 第三階段：解析與決策 (Router/Brain) - 對話與工具路由器。
/// 一般輸入直接生成對話回覆，只有明確工具需求時才產生工具 JSON 命令分派至 JsonCommand 通道。
/// </summary>
public class IntentParserWorker : BackgroundService
{
    private const int RouterOutputTokens = 96;
    private const int AnswerOutputTokens = 300;
    private const int TokenSafetyMargin = 16;
    private const string MinimalRouterTemplate =
        "<|system|>\n{SystemPrompt}<|end|>\n<|user|>{InputText}<|end|>\n<|assistant|>";

    private readonly ILogger<IntentParserWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPhi35ModelService _modelService;
    private readonly PinyinCorrectionService _pinyinService;
    private readonly HashSet<string> _availableToolNames;
    private readonly string _externalToolCatalog;
    private readonly ChannelReader<string> _cleanTextReader;
    private readonly ChannelWriter<string> _jsonCommandWriter;
    private readonly ChannelWriter<string> _answerWriter;
    private readonly RecentConversationService _recentConversation;
    private readonly int _contextLimit;
    private readonly double _answerRepetitionPenalty;

    /// <summary>
    /// 初始化 <see cref="IntentParserWorker"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="configuration">應用程式組態。</param>
    /// <param name="modelService">共享的 Phi-3.5 模型服務。</param>
    /// <param name="pinyinService">拼音校正服務。</param>
    /// <param name="recentConversation">最近對話服務。</param>
    /// <param name="tools">內建靜態 IMcpTool 集合。</param>
    /// <param name="toolManifestCatalog">插件目錄管理員。</param>
    /// <param name="cleanTextChannel">CleanText 核心文字通道。</param>
    /// <param name="jsonCommandChannel">JsonCommand 工具指令通道。</param>
    /// <param name="executionResultChannel">ExecutionResult 執行與對話回應通道。</param>
    public IntentParserWorker(
        ILogger<IntentParserWorker> logger,
        IConfiguration configuration,
        IPhi35ModelService modelService,
        PinyinCorrectionService pinyinService,
        RecentConversationService recentConversation,
        IEnumerable<IMcpTool> tools,
        ToolManifestCatalog toolManifestCatalog,
        [FromKeyedServices("CleanText")] Channel<string> cleanTextChannel,
        [FromKeyedServices("JsonCommand")] Channel<string> jsonCommandChannel,
        [FromKeyedServices("ExecutionResult")] Channel<string> executionResultChannel)
    {
        _logger = logger;
        _configuration = configuration;
        _modelService = modelService;
        _pinyinService = pinyinService;
        _recentConversation = recentConversation;
        _availableToolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        _availableToolNames.UnionWith(toolManifestCatalog.Tools.Select(tool => tool.Manifest.Id));
        _externalToolCatalog = toolManifestCatalog.BuildRouterCatalog();
        _cleanTextReader = cleanTextChannel.Reader;
        _jsonCommandWriter = jsonCommandChannel.Writer;
        _answerWriter = executionResultChannel.Writer;
        _contextLimit = int.TryParse(configuration["OnnxSettings:Phi35:MaxContextLimit"], out int limit)
            ? limit
            : 512;
        _answerRepetitionPenalty = double.TryParse(
            configuration["OnnxSettings:Phi35:AnswerRepetitionPenalty"],
            CultureInfo.InvariantCulture,
            out double repetitionPenalty)
            ? Math.Max(1d, repetitionPenalty)
            : 1.1d;
    }

    /// <summary>
    /// 背景執行迴圈：讀取 CleanText 文字，注入歷史對話上下文，透過 LLM 路由器決策走向一般聊天或工具指令分派。
    /// </summary>
    /// <param name="stoppingToken">取消語彙基元。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Decision Router starting with a {limit}-token context limit...", _contextLimit);

        try
        {
            await foreach (string text in _cleanTextReader.ReadAllAsync(stoppingToken))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;
                try
                {
                    string contextualInput = AddRecentContext(text, _recentConversation.BuildPromptContext(text));
                    _recentConversation.BeginTurn(text);

                    RouterDecision decision = await DecideAsync(text, stoppingToken);
                    _logger.LogInformation(
                        "Router decision for '{text}': {mode}, subject: {subject}",
                        text,
                        decision.Mode,
                        decision.Subject);

                    switch (decision.Mode)
                    {
                        case "conversation":
                            await WriteResponseAsync(contextualInput, "ChatAnswer", "Chat", stoppingToken);
                            break;

                        case "tool":
                            await DispatchToolAsync(decision, stoppingToken);
                            break;

                        default:
                            _logger.LogWarning("Unknown router mode: {mode}; falling back to conversation.", decision.Mode);
                            await WriteResponseAsync(contextualInput, "ChatAnswer", "Chat", stoppingToken);
                            break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Decision Router failed for input: {text}", text);
                    await WriteFinalTextAsync("抱歉，這次指令處理失敗，請再試一次。", stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Decision Router stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Decision Router failed.");
        }
    }

    /// <summary>
    /// 調用 LLM 路由器進行意圖分析，判斷為一般對話或是特定工具調用。
    /// </summary>
    /// <param name="text">使用者輸入文字。</param>
    /// <param name="ct">取消語彙基元。</param>
    /// <returns>路由器決策結果 <see cref="RouterDecision"/>。</returns>
    private async Task<RouterDecision> DecideAsync(string text, CancellationToken ct)
    {
        string systemPrompt = _configuration["PromptSettings:DecisionRouter:SystemPrompt"] ?? "";
        if (!string.IsNullOrWhiteSpace(_externalToolCatalog))
        {
            systemPrompt = $"{systemPrompt}\n{_externalToolCatalog}";
        }
        string userTemplate = _configuration["PromptSettings:DecisionRouter:UserTemplate"] ?? "";
        string prompt;
        try
        {
            prompt = BuildPromptWithinBudget(systemPrompt, userTemplate, text, RouterOutputTokens);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Router few-shot template exceeded the token budget; using the minimal template.");
            prompt = BuildPromptWithinBudget(
                systemPrompt,
                MinimalRouterTemplate,
                text,
                RouterOutputTokens);
        }
        string response = await RunInferenceAsync(prompt, RouterOutputTokens, ct);

        Console.WriteLine($"\n[3. Decision Router]:\n{response.Trim()}");

        if (!TryExtractJson(response, out JsonDocument document))
        {
            _logger.LogWarning("Router returned invalid JSON: {response}", response);
            return RouterDecision.Conversation();
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string mode = root.TryGetProperty("mode", out JsonElement modeElement)
                ? modeElement.GetString()?.Trim().ToLowerInvariant() ?? ""
                : "";
            string subject = root.TryGetProperty("subject", out JsonElement subjectElement)
                ? subjectElement.GetString()?.Trim() ?? "unknown"
                : "unknown";
            string? tool = root.TryGetProperty("tool", out JsonElement toolElement)
                ? toolElement.GetString()?.Trim()
                : null;

            if (mode != "tool")
            {
                return new RouterDecision("conversation", subject, null, null);
            }

            if (string.IsNullOrWhiteSpace(tool) || !_availableToolNames.Contains(tool))
            {
                _logger.LogWarning(
                    "Router selected an unavailable or missing tool: {tool}; falling back to conversation.",
                    tool);
                return RouterDecision.Conversation(subject);
            }

            return new RouterDecision("tool", subject, tool, root.GetRawText());
        }
    }

    /// <summary>
    /// 調用 LLM 生成一般聊天回覆並輸出至回應通道。
    /// </summary>
    /// <param name="text">包含上下文的使用者輸入。</param>
    /// <param name="promptSection">組態中的 Prompt 設定區段名稱。</param>
    /// <param name="outputLabel">Console 輸出的標籤名稱。</param>
    /// <param name="ct">取消語彙基元。</param>
    private async Task WriteResponseAsync(
        string text,
        string promptSection,
        string outputLabel,
        CancellationToken ct)
    {
        string systemPrompt = _configuration[$"PromptSettings:{promptSection}:SystemPrompt"] ?? "";
        string userTemplate = _configuration[$"PromptSettings:{promptSection}:UserTemplate"] ?? "";
        string prompt = BuildPromptWithinBudget(systemPrompt, userTemplate, text, AnswerOutputTokens);
        string answer = CleanModelText(await RunInferenceAsync(
            prompt,
            AnswerOutputTokens,
            ct,
            _answerRepetitionPenalty));

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = "抱歉，我目前無法產生回答。";
        }

        Console.WriteLine($"\n[3. {outputLabel}]: {answer}");
        await WriteFinalTextAsync(answer, ct);
    }

    /// <summary>
    /// 將路由器決策的工具 JSON 指令進行拼音校正並寫入 JsonCommand 通道。
    /// </summary>
    /// <param name="decision">路由器決策結果。</param>
    /// <param name="ct">取消語彙基元。</param>
    private async Task DispatchToolAsync(RouterDecision decision, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(decision.CommandJson) ||
            string.IsNullOrWhiteSpace(decision.Tool) ||
            !_availableToolNames.Contains(decision.Tool))
        {
            _logger.LogWarning("Router produced an invalid tool command.");
            await WriteFinalTextAsync("目前無法建立有效的工具指令。", ct);
            return;
        }

        string commandJson = _pinyinService.CorrectJsonValues(decision.CommandJson);
        Console.WriteLine($"\n[3. Tool Command]: {commandJson}");
        await _jsonCommandWriter.WriteAsync(commandJson, ct);
    }

    /// <summary>
    /// 將最終文字回覆寫入 ExecutionResult 通道並通知對話記憶服務完成回合。
    /// </summary>
    /// <param name="text">回覆文字。</param>
    /// <param name="ct">取消語彙基元。</param>
    private async Task WriteFinalTextAsync(string text, CancellationToken ct)
    {
        await _answerWriter.WriteAsync(text, ct);
        _recentConversation.CompleteTurn(text);
    }

    /// <summary>
    /// 將歷史對話上下文與當前輸入合併為完整 Prompt 輸入文字。
    /// </summary>
    /// <param name="currentInput">當前輸入。</param>
    /// <param name="recentContext">歷史對話上下文。</param>
    /// <returns>合併後的輸入字串。</returns>
    private static string AddRecentContext(string currentInput, string recentContext)
    {
        return string.IsNullOrWhiteSpace(recentContext)
            ? currentInput
            : $"以下是先前對話，只用來理解上下文：\n{recentContext}\n\n目前使用者輸入（請以這句為主）：\n{currentInput}";
    }

    /// <summary>
    /// 在 Token 預算限制內動態構建 Prompt，必要時自動縮減使用者輸入長度以防超出 Context Window。
    /// </summary>
    /// <param name="systemPrompt">系統提示詞。</param>
    /// <param name="userTemplate">使用者樣板。</param>
    /// <param name="inputText">輸入文字內容。</param>
    /// <param name="reservedOutputTokens">預留給輸出的 Token 數量。</param>
    /// <returns>編碼合規的 Prompt 字串。</returns>
    private string BuildPromptWithinBudget(
        string systemPrompt,
        string userTemplate,
        string inputText,
        int reservedOutputTokens)
    {
        int maxInputTokens = _contextLimit - reservedOutputTokens - TokenSafetyMargin;
        string candidateInput = inputText.Trim();

        while (true)
        {
            string prompt = userTemplate
                .Replace("{SystemPrompt}", systemPrompt)
                .Replace("{InputText}", candidateInput);

            using var sequences = _modelService.Tokenizer.Encode(prompt);
            if (sequences[0].Length <= maxInputTokens)
            {
                if (candidateInput.Length < inputText.Trim().Length)
                {
                    _logger.LogWarning(
                        "Input was shortened to fit the {limit}-token context budget.",
                        _contextLimit);
                }

                return prompt;
            }

            if (candidateInput.Length <= 16)
            {
                throw new InvalidOperationException(
                    $"Prompt template exceeds the {_contextLimit}-token context budget.");
            }

            candidateInput = candidateInput[..Math.Max(16, candidateInput.Length * 3 / 4)].TrimEnd();
        }
    }

    /// <summary>
    /// 執行 ONNX GenAI 模型推論，支援 Repetition Penalty、動態長度截斷與結束符號偵測。
    /// </summary>
    /// <param name="prompt">完整的輸入 Prompt。</param>
    /// <param name="reservedOutputTokens">最多生成的 Output Token 數。</param>
    /// <param name="ct">取消語彙基元。</param>
    /// <param name="repetitionPenalty">重複懲罰係數。</param>
    /// <returns>模型生成的文字內容。</returns>
    private async Task<string> RunInferenceAsync(
        string prompt,
        int reservedOutputTokens,
        CancellationToken ct,
        double repetitionPenalty = 1d)
    {
        bool lockTaken = false;
        try
        {
            await _modelService.Lock.WaitAsync(ct);
            lockTaken = true;

            using var generatorParams = new GeneratorParams(_modelService.Model);
            using var sequences = _modelService.Tokenizer.Encode(prompt);
            int inputTokens = sequences[0].Length;
            int maxLength = Math.Min(inputTokens + reservedOutputTokens, _contextLimit);

            generatorParams.SetSearchOption("max_length", maxLength);
            generatorParams.SetSearchOption("do_sample", false);
            generatorParams.SetSearchOption("repetition_penalty", repetitionPenalty);
            generatorParams.SetSearchOption("past_present_share_buffer", true);

            using var generator = new Generator(_modelService.Model, generatorParams);
            generator.AppendTokenSequences(sequences);
            using var tokenizerStream = _modelService.Tokenizer.CreateStream();

            string result = "";
            while (!generator.IsDone() && !ct.IsCancellationRequested)
            {
                generator.GenerateNextToken();
                int lastTokenId = generator.GetSequence(0)[^1];
                string part = tokenizerStream.Decode(lastTokenId);

                if (!string.IsNullOrEmpty(part))
                {
                    result += part;
                    if (repetitionPenalty > 1d && TryTrimRepeatedSuffix(result, out string trimmedResult))
                    {
                        _logger.LogWarning("Answer generation stopped after detecting a repeated suffix.");
                        return trimmedResult;
                    }
                    int endIndex = result.IndexOf("[END]", StringComparison.Ordinal);
                    if (endIndex >= 0)
                    {
                        return result[..endIndex];
                    }
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Inference sub-call failed.");
            return "";
        }
        finally
        {
            if (lockTaken)
            {
                _modelService.Lock.Release();
            }
        }
    }

    /// <summary>
    /// 嘗試從模型輸出字串中提取合法的 JSON 物件。
    /// </summary>
    /// <param name="text">模型輸出字串。</param>
    /// <param name="document">解析成功的 JsonDocument。</param>
    /// <returns>若成功解析出 JSON 物件則回傳 true，否則為 false。</returns>
    private static bool TryExtractJson(string text, out JsonDocument document)
    {
        document = null!;
        int start = text.IndexOf('{');
        int end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return false;

        try
        {
            document = JsonDocument.Parse(text[start..(end + 1)]);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return true;
            }

            document.Dispose();
            document = null!;
            return false;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    /// <summary>
    /// 清理模型回覆文字中的標記符號（如 [CLEAN]、[END]、&lt;|end|&gt; 等）。
    /// </summary>
    /// <param name="text">原始文字。</param>
    /// <returns>清理後的純文字。</returns>
    private static string CleanModelText(string text)
    {
        return text
            .Replace("[CLEAN]", "", StringComparison.Ordinal)
            .Replace("[END]", "", StringComparison.Ordinal)
            .Replace("<|end|>", "", StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// 偵測並修剪模型生成過程中的重複後綴跳針字句。
    /// </summary>
    /// <param name="text">當前生成的文字。</param>
    /// <param name="trimmed">修剪後的文字。</param>
    /// <returns>若偵測到重複跳針並完成修剪則回傳 true，否則為 false。</returns>
    private static bool TryTrimRepeatedSuffix(string text, out string trimmed)
    {
        const int repetitions = 4;
        trimmed = text;
        if (text.Length < 24) return false;

        for (int phraseLength = 2; phraseLength <= 24; phraseLength++)
        {
            int repeatedLength = phraseLength * repetitions;
            if (repeatedLength > text.Length) break;

            string phrase = text[^phraseLength..];
            bool repeated = true;
            for (int index = 2; index <= repetitions; index++)
            {
                int start = text.Length - phraseLength * index;
                if (!text.AsSpan(start, phraseLength).SequenceEqual(phrase))
                {
                    repeated = false;
                    break;
                }
            }

            if (!repeated) continue;
            trimmed = text[..(text.Length - repeatedLength + phraseLength)].TrimEnd();
            return true;
        }

        return false;
    }

    /// <summary>
    /// 路由器決策記錄。
    /// </summary>
    /// <param name="Mode">決策模式（"conversation" 或 "tool"）。</param>
    /// <param name="Subject">主題摘要。</param>
    /// <param name="Tool">若為工具模式，代表工具名稱。</param>
    /// <param name="CommandJson">若為工具模式，代表工具執行的完整 JSON 字串。</param>
    private sealed record RouterDecision(
        string Mode,
        string Subject,
        string? Tool,
        string? CommandJson)
    {
        public static RouterDecision Conversation(string subject = "unknown") =>
            new("conversation", subject, null, null);
    }
}
