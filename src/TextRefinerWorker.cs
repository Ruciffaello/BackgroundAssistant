using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

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
                    // 獲取模型排隊鎖，避免多個工作者同時爭搶推論資源
                    await _modelService.Lock.WaitAsync(stoppingToken);

                    // 從設定檔讀取提示詞
                    string sysPrompt = _configuration["PromptSettings:TextRefiner:SystemPrompt"] ?? "";
                    string userTemplate = _configuration["PromptSettings:TextRefiner:UserTemplate"] ?? "";
                    
                    string prompt = userTemplate
                        .Replace("{SystemPrompt}", sysPrompt)
                        .Replace("{InputText}", rawText);

                    using var generatorParams = new GeneratorParams(_modelService.Model);
                    using var sequences = _modelService.Tokenizer.Encode(prompt);
                    
                    // 設定推論參數 (Greedy Search 以獲得穩定的結果)
                    generatorParams.SetSearchOption("max_length", 512);
                    generatorParams.SetSearchOption("do_sample", false);
                    generatorParams.SetSearchOption("past_present_share_buffer", true);

                    using var generator = new Generator(_modelService.Model, generatorParams);
                    generator.AppendTokenSequences(sequences);
                    
                    using var tokenizerStream = _modelService.Tokenizer.CreateStream();
                    
                    while (!generator.IsDone())
                    {
                        generator.GenerateNextToken();
                        var lastTokenId = generator.GetSequence(0)[^1];
                        var part = tokenizerStream.Decode(lastTokenId);
                        
                        if (!string.IsNullOrEmpty(part))
                        {
                            // 偵測到結束標籤即停止，避免 AI 產生幻覺
                            if (part.Contains("[END]")) break;
                            refinedText += part;
                        }
                    }
                    
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
                finally
                {
                    _modelService.Lock.Release();
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
