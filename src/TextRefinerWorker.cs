using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant;

/// <summary>
/// 第二階段：精煉 (Refiner) - 文字潤飾工作者。
/// 負責從 RawText 通道讀取 STT 的原始結果，使用 Phi-3.5 模型移除語音贅字（如：那個、呃、啊），
/// 並輸出核心語意文字到 CleanText 通道。
/// </summary>
public class TextRefinerWorker : BackgroundService
{
    private readonly ILogger<TextRefinerWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPhi35ModelService _modelService;
    private readonly ChannelReader<string> _rawTextReader;
    private readonly ChannelWriter<string> _cleanTextWriter;

    /// <summary>
    /// 初始化 <see cref="TextRefinerWorker"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="configuration">應用程式組態。</param>
    /// <param name="modelService">共享的 Phi-3.5 模型服務。</param>
    /// <param name="rawTextChannel">原始語音文字通道。</param>
    /// <param name="cleanTextChannel">精煉後核心文字通道。</param>
    public TextRefinerWorker(
        ILogger<TextRefinerWorker> logger, 
        IConfiguration configuration,
        IPhi35ModelService modelService,
        [FromKeyedServices("RawText")] Channel<string> rawTextChannel, 
        [FromKeyedServices("CleanText")] Channel<string> cleanTextChannel)
    {
        _logger = logger;
        _configuration = configuration;
        _modelService = modelService;
        _rawTextReader = rawTextChannel.Reader;
        _cleanTextWriter = cleanTextChannel.Writer;
    }

    /// <summary>
    /// 背景執行迴圈：從 RawText 讀取字串，調用 LLM 移除贅字並輸出至 CleanText。
    /// </summary>
    /// <param name="stoppingToken">取消語彙基元。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Text Refiner Worker starting (Shared Session)...");

        try
        {
            await foreach (var rawText in _rawTextReader.ReadAllAsync(stoppingToken))
            {
                if (string.IsNullOrWhiteSpace(rawText)) continue;

                _logger.LogInformation("Refining text: {text}", rawText);

                string refinedText = "";
                try
                {
                    // 從設定檔讀取提示詞
                    string sysPrompt = _configuration["PromptSettings:TextRefiner:SystemPrompt"] ?? "";
                    string userTemplate = _configuration["PromptSettings:TextRefiner:UserTemplate"] ?? "";
                    
                    string prompt = userTemplate
                        .Replace("{SystemPrompt}", sysPrompt)
                        .Replace("{InputText}", rawText);

                    Phi35GenerationResult generation = await _modelService.GenerateAsync(
                        new Phi35GenerationRequest(
                            prompt,
                            ContextLimit: 512,
                            MaxOutputTokens: 32,
                            MinimumTotalTokens: 64),
                        stoppingToken);
                    refinedText = generation.Text;
                    
                    // 使用正則表達式精準抓取 [CLEAN] 與 [END] 之間的內容
                    var match = System.Text.RegularExpressions.Regex.Match(refinedText, @"\[CLEAN\](.*?)\[END\]", System.Text.RegularExpressions.RegexOptions.Singleline);
                    if (match.Success)
                    {
                        refinedText = match.Groups[1].Value.Trim();
                    }
                    else
                    {
                        // 清理殘留的標籤內容 (Fallback)
                        refinedText = refinedText.Replace("[CLEAN]", "").Replace("[END]", "").Trim();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Refinement failed for text: {text}", rawText);
                    refinedText = rawText; // 失敗時退回原始文字
                }
                // 再次確保只取第一行，防止模型幻覺出的解釋文字
                refinedText = refinedText.Split('\n')[0].Trim();
                if (string.IsNullOrWhiteSpace(refinedText)) refinedText = rawText;

                Console.WriteLine($"[2. Refined Text]: {refinedText}");
                // 送往解析階段
                await _cleanTextWriter.WriteAsync(refinedText, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Text Refiner stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Text Refiner failed to start.");
            await Task.Delay(-1, stoppingToken);
        }
    }
}
