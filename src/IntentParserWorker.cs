using System.Threading.Channels;
using System.Globalization;
using BackgroundAssistant.Services;
using BackgroundAssistant.Memory;
using BackgroundAssistant.Prompting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundAssistant;

/// <summary>
/// 第三階段：解析與決策 (Router/Brain) - 對話與工具路由器。
/// 一般輸入直接生成對話回覆，只有明確工具需求時才產生工具 JSON 命令分派至 JsonCommand 通道。
/// </summary>
public class IntentParserWorker : BackgroundService
{
    private const int AnswerOutputTokens = 300;
    private const int TokenSafetyMargin = 16;

    private readonly ILogger<IntentParserWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPhi35ModelService _modelService;
    private readonly IntentRouter _intentRouter;
    private readonly PinyinCorrectionService _pinyinService;
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
    /// <param name="intentRouter">單次對話／工具路由器。</param>
    /// <param name="pinyinService">拼音校正服務。</param>
    /// <param name="recentConversation">最近對話服務。</param>
    /// <param name="cleanTextChannel">CleanText 核心文字通道。</param>
    /// <param name="jsonCommandChannel">JsonCommand 工具指令通道。</param>
    /// <param name="executionResultChannel">ExecutionResult 執行與對話回應通道。</param>
    public IntentParserWorker(
        ILogger<IntentParserWorker> logger,
        IConfiguration configuration,
        IPhi35ModelService modelService,
        IntentRouter intentRouter,
        PinyinCorrectionService pinyinService,
        RecentConversationService recentConversation,
        [FromKeyedServices("CleanText")] Channel<string> cleanTextChannel,
        [FromKeyedServices("JsonCommand")] Channel<string> jsonCommandChannel,
        [FromKeyedServices("ExecutionResult")] Channel<string> executionResultChannel)
    {
        _logger = logger;
        _configuration = configuration;
        _modelService = modelService;
        _intentRouter = intentRouter;
        _pinyinService = pinyinService;
        _recentConversation = recentConversation;
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
                    string recentContext = _recentConversation.BuildPromptContext(text);
                    _recentConversation.BeginTurn(text);

                    RouterDecision decision = await _intentRouter.DecideAsync(text, stoppingToken);
                    _logger.LogInformation(
                        "Router decision for '{text}': {mode}, subject: {subject}",
                        text,
                        decision.Mode,
                        decision.Subject);

                    switch (decision.Mode)
                    {
                        case "conversation":
                            await WriteResponseAsync(text, recentContext, "ChatAnswer", "Chat", stoppingToken);
                            break;

                        case "tool":
                            await DispatchToolAsync(decision, stoppingToken);
                            break;

                        default:
                            _logger.LogWarning("Unknown router mode: {mode}; falling back to conversation.", decision.Mode);
                            await WriteResponseAsync(text, recentContext, "ChatAnswer", "Chat", stoppingToken);
                            break;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (PromptInputTooLongException ex)
                {
                    _logger.LogWarning(ex, "Current input does not fit the configured model context budget.");
                    await WriteFinalTextAsync("這段訊息太長，請拆成較短的內容再試一次。", stoppingToken);
                }
                catch (PromptTemplateTooLongException ex)
                {
                    _logger.LogError(ex, "Configured prompt template does not fit the model context budget.");
                    await WriteFinalTextAsync("系統提示設定超過模型可處理的長度，請調整設定後再試一次。", stoppingToken);
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
    /// 調用 LLM 生成一般聊天回覆並輸出至回應通道。
    /// </summary>
    /// <param name="currentInput">目前使用者輸入。</param>
    /// <param name="recentContext">已篩選的相關歷史上下文。</param>
    /// <param name="promptSection">組態中的 Prompt 設定區段名稱。</param>
    /// <param name="outputLabel">Console 輸出的標籤名稱。</param>
    /// <param name="ct">取消語彙基元。</param>
    private async Task WriteResponseAsync(
        string currentInput,
        string recentContext,
        string promptSection,
        string outputLabel,
        CancellationToken ct)
    {
        string systemPrompt = _configuration[$"PromptSettings:{promptSection}:SystemPrompt"] ?? "";
        string userTemplate = _configuration[$"PromptSettings:{promptSection}:UserTemplate"] ?? "";
        string prompt = BuildPromptWithinBudget(
            systemPrompt,
            userTemplate,
            currentInput,
            recentContext,
            AnswerOutputTokens);
        string answer = CleanModelText((await _modelService.GenerateAsync(
            new Phi35GenerationRequest(
                prompt,
                _contextLimit,
                AnswerOutputTokens,
                RepetitionPenalty: _answerRepetitionPenalty,
                DetectRepeatedSuffix: true),
            ct)).Text);

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
            string.IsNullOrWhiteSpace(decision.Tool))
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
    /// 在 Token 預算限制內建立 Prompt。歷史上下文超限時會略過，保留目前使用者輸入。
    /// </summary>
    /// <param name="systemPrompt">系統提示詞。</param>
    /// <param name="userTemplate">使用者樣板。</param>
    /// <param name="currentInput">目前使用者輸入。</param>
    /// <param name="recentContext">已篩選的相關歷史上下文。</param>
    /// <param name="reservedOutputTokens">預留給輸出的 Token 數量。</param>
    /// <returns>編碼合規的 Prompt 字串。</returns>
    private string BuildPromptWithinBudget(
        string systemPrompt,
        string userTemplate,
        string currentInput,
        string? recentContext,
        int reservedOutputTokens)
    {
        PromptBudgetResult result = PromptBudgetBuilder.Build(
            systemPrompt,
            userTemplate,
            currentInput,
            recentContext,
            _contextLimit,
            reservedOutputTokens,
            TokenSafetyMargin,
            CountTokens);

        if (result.RecentContextOmitted)
        {
            _logger.LogWarning(
                "Recent context was omitted to keep the current input within the {limit}-token context budget.",
                _contextLimit);
        }

        return result.Prompt;
    }

    private int CountTokens(string text)
    {
        return _modelService.CountTokens(text);
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

}
