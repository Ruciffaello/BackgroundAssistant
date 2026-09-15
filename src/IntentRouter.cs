using System.Text.Json;
using BackgroundAssistant.PluginRuntime;
using BackgroundAssistant.Prompting;
using BackgroundAssistant.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant;

/// <summary>
/// 以單次模型推論決定一般對話或工具呼叫，並驗證模型輸出的工具名稱。
/// </summary>
public sealed class IntentRouter
{
    private const int RouterOutputTokens = 96;
    private const int TokenSafetyMargin = 16;
    private const string MinimalRouterTemplate =
        "<|system|>\n{SystemPrompt}<|end|>\n<|user|>{InputText}<|end|>\n<|assistant|>";

    private readonly ILogger<IntentRouter> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPhi35ModelService _modelService;
    private readonly HashSet<string> _availableToolNames;
    private readonly string _externalToolCatalog;
    private readonly int _contextLimit;

    public IntentRouter(
        ILogger<IntentRouter> logger,
        IConfiguration configuration,
        IPhi35ModelService modelService,
        IEnumerable<IMcpTool> tools,
        ToolManifestCatalog toolManifestCatalog)
    {
        _logger = logger;
        _configuration = configuration;
        _modelService = modelService;
        _availableToolNames = tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        _availableToolNames.UnionWith(toolManifestCatalog.Tools.Select(tool => tool.Manifest.Id));
        _externalToolCatalog = toolManifestCatalog.BuildRouterCatalog();
        _contextLimit = int.TryParse(configuration["OnnxSettings:Phi35:MaxContextLimit"], out int limit)
            ? limit
            : 512;
    }

    public async Task<RouterDecision> DecideAsync(string text, CancellationToken cancellationToken)
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
            prompt = BuildPrompt(systemPrompt, userTemplate, text);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(
                ex,
                "Router few-shot template exceeded the token budget; using the minimal template.");
            prompt = BuildPrompt(systemPrompt, MinimalRouterTemplate, text);
        }

        string response = (await _modelService.GenerateAsync(
            new Phi35GenerationRequest(prompt, _contextLimit, RouterOutputTokens),
            cancellationToken)).Text;

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
                return RouterDecision.Conversation(subject);
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

    private string BuildPrompt(string systemPrompt, string userTemplate, string inputText)
    {
        return PromptBudgetBuilder.Build(
            systemPrompt,
            userTemplate,
            inputText,
            null,
            _contextLimit,
            RouterOutputTokens,
            TokenSafetyMargin,
            _modelService.CountTokens).Prompt;
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
}

/// <summary>
/// Router 的結構化決策；工具模式攜帶原始 JSON 供現有 Executor 使用。
/// </summary>
public sealed record RouterDecision(string Mode, string Subject, string? Tool, string? CommandJson)
{
    public static RouterDecision Conversation(string subject = "unknown") =>
        new("conversation", subject, null, null);
}
