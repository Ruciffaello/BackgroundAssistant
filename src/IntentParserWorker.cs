using System.Text.Json;
using System.Threading.Channels;
using System.Globalization;
using BackgroundAssistant.Services;
using BackgroundAssistant.Tools;
using BackgroundAssistant.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace BackgroundAssistant;

/// <summary>
/// 對話／工具路由器：一般輸入直接對話，只有明確工具需求才產生工具命令。
/// </summary>
public class IntentParserWorker : BackgroundService
{
    private const int RouterOutputTokens = 96;
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
    private readonly RecentConversationService _recentConversation;
    private readonly int _contextLimit;
    private readonly double _answerRepetitionPenalty;

    public IntentParserWorker(
        ILogger<IntentParserWorker> logger,
        IConfiguration configuration,
        IPhi35ModelService modelService,
        PinyinCorrectionService pinyinService,
        RecentConversationService recentConversation,
        IEnumerable<IMcpTool> tools,
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Decision Router starting with a {limit}-token context limit...", _contextLimit);

        try
        {
            await foreach (string text in _cleanTextReader.ReadAllAsync(stoppingToken))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;

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

    private async Task WriteFinalTextAsync(string text, CancellationToken ct)
    {
        await _answerWriter.WriteAsync(text, ct);
        _recentConversation.CompleteTurn(text);
    }

    private static string AddRecentContext(string currentInput, string recentContext)
    {
        return string.IsNullOrWhiteSpace(recentContext)
            ? currentInput
            : $"以下是先前對話，只用來理解上下文：\n{recentContext}\n\n目前使用者輸入（請以這句為主）：\n{currentInput}";
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
