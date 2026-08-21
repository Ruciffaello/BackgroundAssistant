using System.Text.Json;
using System.Threading.Channels;
using BackgroundAssistant.Services;
using BackgroundAssistant.Tools;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace BackgroundAssistant;

/// <summary>
/// 決策路由器：判斷輸入應直接回答、檢索資料、使用工具或要求澄清。
/// RAG 與 Memory Provider 尚未接入；retrieve 目前會安全地要求使用者補充資訊。
/// </summary>
public class IntentParserWorker : BackgroundService
{
    private const int RouterOutputTokens = 48;
    private const int ToolPlannerOutputTokens = 96;
    private const int AnswerOutputTokens = 300;
    private const int TokenSafetyMargin = 16;

    private readonly ILogger<IntentParserWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPhi35ModelService _modelService;
    private readonly PinyinCorrectionService _pinyinService;
    private readonly HashSet<string> _availableToolNames;
    private readonly ChannelReader<string> _cleanTextReader;
    private readonly ChannelWriter<string> _jsonCommandWriter;
    private readonly ChannelWriter<string> _answerWriter;
    private readonly int _contextLimit;

    public IntentParserWorker(
        ILogger<IntentParserWorker> logger,
        IConfiguration configuration,
        IPhi35ModelService modelService,
        PinyinCorrectionService pinyinService,
        IEnumerable<IMcpTool> tools,
        [FromKeyedServices("CleanText")] Channel<string> cleanTextChannel,
        [FromKeyedServices("JsonCommand")] Channel<string> jsonCommandChannel,
        [FromKeyedServices("ExecutionResult")] Channel<string> executionResultChannel)
    {
        _logger = logger;
        _configuration = configuration;
        _modelService = modelService;
        _pinyinService = pinyinService;
        _availableToolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        _cleanTextReader = cleanTextChannel.Reader;
        _jsonCommandWriter = jsonCommandChannel.Writer;
        _answerWriter = executionResultChannel.Writer;
        _contextLimit = int.TryParse(configuration["OnnxSettings:Phi35:MaxContextLimit"], out int limit)
            ? limit
            : 512;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Decision Router starting with a {limit}-token context limit...", _contextLimit);

        try
        {
            await foreach (string text in _cleanTextReader.ReadAllAsync(stoppingToken))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;

                RouterDecision decision = await DecideAsync(text, stoppingToken);
                _logger.LogInformation("Router decision for '{text}': {action}", text, decision.Action);

                switch (decision.Action)
                {
                    case "answer":
                        await WriteResponseAsync(text, "DirectAnswer", "Direct Answer", stoppingToken);
                        break;

                    case "chat":
                        await WriteResponseAsync(text, "ChatAnswer", "Chat", stoppingToken);
                        break;

                    case "support":
                        await WriteResponseAsync(text, "SupportAnswer", "Emotional Support", stoppingToken);
                        break;

                    case "tool":
                        await PlanToolAsync(text, stoppingToken);
                        break;

                    case "clarify":
                        await _answerWriter.WriteAsync(
                            string.IsNullOrWhiteSpace(decision.Question)
                                ? "請再多提供一些資訊，讓我知道你希望我做什麼。"
                                : decision.Question,
                            stoppingToken);
                        break;

                    case "retrieve":
                        _logger.LogInformation(
                            "Retrieval requested for source {source}, but no Memory/RAG provider is registered yet.",
                            decision.Source);
                        await _answerWriter.WriteAsync(
                            "我目前還沒有可用的記憶或知識庫資料，請再提供一些相關資訊。",
                            stoppingToken);
                        break;

                    default:
                        _logger.LogWarning("Unknown router action: {action}", decision.Action);
                        await _answerWriter.WriteAsync("請再說明你希望我回答或執行的內容。", stoppingToken);
                        break;
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

    private async Task<RouterDecision> DecideAsync(string text, CancellationToken ct)
    {
        string systemPrompt = _configuration["PromptSettings:DecisionRouter:SystemPrompt"] ?? "";
        string userTemplate = _configuration["PromptSettings:DecisionRouter:UserTemplate"] ?? "";
        string prompt = BuildPromptWithinBudget(systemPrompt, userTemplate, text, RouterOutputTokens);
        string response = await RunInferenceAsync(prompt, RouterOutputTokens, ct);

        if (!TryExtractJson(response, out JsonDocument document))
        {
            _logger.LogWarning("Router returned invalid JSON: {response}", response);
            return new RouterDecision("clarify", "請再說明你希望我做什麼。", null, null);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            string action = root.TryGetProperty("action", out JsonElement actionElement)
                ? actionElement.GetString()?.Trim().ToLowerInvariant() ?? ""
                : "";
            string? question = root.TryGetProperty("question", out JsonElement questionElement)
                ? questionElement.GetString()
                : null;
            string? source = root.TryGetProperty("source", out JsonElement sourceElement)
                ? sourceElement.GetString()
                : null;
            string? query = root.TryGetProperty("query", out JsonElement queryElement)
                ? queryElement.GetString()
                : null;

            if (action == "retrieve" && source is not ("memory" or "rag"))
            {
                _logger.LogWarning("Router requested unsupported retrieval source: {source}", source);
                return new RouterDecision(
                    "clarify",
                    string.IsNullOrWhiteSpace(question) ? "你想查詢什麼內容？" : question,
                    null,
                    null);
            }

            return action is "answer" or "chat" or "support" or "retrieve" or "tool" or "clarify"
                ? new RouterDecision(action, question, source, query)
                : new RouterDecision("clarify", "請再說明你希望我做什麼。", null, null);
        }
    }

    private async Task WriteResponseAsync(
        string text,
        string promptSection,
        string outputLabel,
        CancellationToken ct)
    {
        string systemPrompt = _configuration[$"PromptSettings:{promptSection}:SystemPrompt"] ?? "";
        string userTemplate = _configuration[$"PromptSettings:{promptSection}:UserTemplate"] ?? "";
        string prompt = BuildPromptWithinBudget(systemPrompt, userTemplate, text, AnswerOutputTokens);
        string answer = CleanModelText(await RunInferenceAsync(prompt, AnswerOutputTokens, ct));

        if (string.IsNullOrWhiteSpace(answer))
        {
            answer = "抱歉，我目前無法產生回答。";
        }

        Console.WriteLine($"\n[3. {outputLabel}]: {answer}");
        await _answerWriter.WriteAsync(answer, ct);
    }

    private async Task PlanToolAsync(string text, CancellationToken ct)
    {
        string systemPrompt = _configuration["PromptSettings:ToolPlanner:SystemPrompt"] ?? "";
        string userTemplate = _configuration["PromptSettings:ToolPlanner:UserTemplate"] ?? "";
        string prompt = BuildPromptWithinBudget(systemPrompt, userTemplate, text, ToolPlannerOutputTokens);
        string response = await RunInferenceAsync(prompt, ToolPlannerOutputTokens, ct);

        if (!TryExtractJson(response, out JsonDocument document))
        {
            _logger.LogWarning("Tool Planner returned invalid JSON: {response}", response);
            await _answerWriter.WriteAsync("我知道這需要使用工具，但目前無法建立有效的工具指令。", ct);
            return;
        }

        string commandJson;
        using (document)
        {
            JsonElement root = document.RootElement;
            string toolName = root.TryGetProperty("tool", out JsonElement toolElement)
                ? toolElement.GetString() ?? ""
                : "";

            if (!_availableToolNames.Contains(toolName))
            {
                _logger.LogWarning("Tool Planner selected unavailable tool: {tool}", toolName);
                await _answerWriter.WriteAsync("目前沒有可執行這項要求的工具。", ct);
                return;
            }

            commandJson = root.GetRawText();
        }

        commandJson = _pinyinService.CorrectJsonValues(commandJson);
        Console.WriteLine($"\n[3. Tool Plan]: {commandJson}");
        await _jsonCommandWriter.WriteAsync(commandJson, ct);
    }

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

    private async Task<string> RunInferenceAsync(
        string prompt,
        int reservedOutputTokens,
        CancellationToken ct)
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

    private static string CleanModelText(string text)
    {
        return text
            .Replace("[CLEAN]", "", StringComparison.Ordinal)
            .Replace("[END]", "", StringComparison.Ordinal)
            .Replace("<|end|>", "", StringComparison.Ordinal)
            .Trim();
    }

    private sealed record RouterDecision(
        string Action,
        string? Question,
        string? Source,
        string? Query);
}
