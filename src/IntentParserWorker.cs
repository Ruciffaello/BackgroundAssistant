using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;
using BackgroundAssistant.Services;

namespace BackgroundAssistant;

/// <summary>
/// 第三階段：解析 (Parser) - 意圖分析工作者。
/// 採用「兩階段解析」架構：
/// 1. 分類器 (Classifier)：判斷使用者意圖大類 (News, Pokemon, Time, Knowledge)。
/// 2. 提取器 (Extractor)：根據大類別，使用專用 Prompt 提取 JSON 參數。
/// </summary>
public class IntentParserWorker : BackgroundService
{
    private readonly ILogger<IntentParserWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IPhi35ModelService _modelService;
    private readonly PinyinCorrectionService _pinyinService;
    private readonly SqliteDatabaseService _sqliteService;
    private readonly ChannelReader<string> _cleanTextReader;
    private readonly ChannelWriter<string> _jsonCommandWriter;

    public IntentParserWorker(
        ILogger<IntentParserWorker> logger, 
        IConfiguration configuration,
        IPhi35ModelService modelService,
        PinyinCorrectionService pinyinService,
        SqliteDatabaseService sqliteService,
        [FromKeyedServices("CleanText")] Channel<string> cleanTextChannel, 
        [FromKeyedServices("JsonCommand")] Channel<string> jsonCommandChannel)
    {
        _logger = logger;
        _configuration = configuration;
        _modelService = modelService;
        _pinyinService = pinyinService;
        _sqliteService = sqliteService;
        _cleanTextReader = cleanTextChannel.Reader;
        _jsonCommandWriter = jsonCommandChannel.Writer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Intent Parser Worker starting (Two-Stage + SQLite Fast Path Enabled)...");

        try
        {
            await foreach (var text in _cleanTextReader.ReadAllAsync(stoppingToken))
            {
                if (string.IsNullOrWhiteSpace(text)) continue;

                // --- 快速路徑：SQLite 關鍵字比對 ---
                _logger.LogInformation("Checking SQLite Fast Path for: {text}", text);
                string? fastPathResult = _sqliteService.GetActionByKeyword(text);
                
                if (fastPathResult != null)
                {
                    _logger.LogInformation("SQLite Match Found! Bypassing LLM inference.");
                    Console.WriteLine($"\n[3. Intent Parser (SQLite Fast Path)]: {fastPathResult}");
                    await _jsonCommandWriter.WriteAsync(fastPathResult, stoppingToken);
                    continue;
                }

                _logger.LogInformation("No SQLite match. Step 1: Classifying intent for: {text}", text);

                // --- 第一階段：分類 ---
                string classifierSys = _configuration["PromptSettings:IntentParser:Classifier:SystemPrompt"] ?? "";
                string classifierUser = _configuration["PromptSettings:IntentParser:Classifier:UserTemplate"] ?? "";
                string classifierPrompt = classifierUser.Replace("{SystemPrompt}", classifierSys).Replace("{InputText}", text);
                
                // 強制分類器也使用 [CLEAN] 標籤約束
                string category = await RunInferenceAsync(classifierPrompt, "[END]", stoppingToken);
                
                // 抓取 [CLEAN] 標籤內的內容
                var catMatch = System.Text.RegularExpressions.Regex.Match(category, @"\[CLEAN\](.*?)\[END\]", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (catMatch.Success)
                {
                    category = catMatch.Groups[1].Value.Trim();
                }
                else
                {
                    // Fallback: 移除所有標籤後取第一行
                    category = category.Replace("[CLEAN]", "").Replace("[END]", "").Trim().Split('\n')[0];
                }

                _logger.LogInformation("Detected Category: {category}", category);

                // --- 第二階段：提取 ---
                string response = "無法執行";

                // 方案 C 保險機制：如果分類為 None，但文字長度符合中文姓名特徵 (2-5字)，強制嘗試 Humor 提取
                if (category == "None" && text.Length >= 2 && text.Length <= 5)
                {
                    _logger.LogInformation("Fallback: Input '{text}' looks like a name. Forcing Humor extraction.", text);
                    category = "Humor";
                }
                
                // 檢查該類別是否有對應的 Extractor
                string extractorPath = $"PromptSettings:IntentParser:Extractors:{category}";
                string extractorSys = _configuration[$"{extractorPath}:SystemPrompt"] ?? "";
                string extractorUser = _configuration[$"{extractorPath}:UserTemplate"] ?? "";

                if (!string.IsNullOrEmpty(extractorSys))
                {
                    _logger.LogInformation("Step 2: Extracting parameters using {category} extractor...", category);
                    string extractorPrompt = extractorUser.Replace("{SystemPrompt}", extractorSys).Replace("{InputText}", text);
                    response = await RunInferenceAsync(extractorPrompt, "[END]", stoppingToken);
                }
                else
                {
                    _logger.LogWarning("No extractor found for category: {category}", category);
                    response = "無法執行";
                }

                // --- 後處理：JSON 抓取與拼音校正 ---
                // 使用非貪婪模式 \{.*?\} 確保只抓取第一組 JSON，避免模型碎碎念
                var match = System.Text.RegularExpressions.Regex.Match(response, @"\{.*?\}", System.Text.RegularExpressions.RegexOptions.Singleline);
                if (match.Success)
                {
                    response = match.Value;
                    string correctedResponse = _pinyinService.CorrectJsonValues(response);
                    if (correctedResponse != response)
                    {
                        _logger.LogInformation("Pinyin Post-Correction applied: {old} -> {new}", response, correctedResponse);
                        response = correctedResponse;
                    }
                }
                else
                {
                    response = "無法執行";
                }

                Console.WriteLine($"\n[3. Intent Parser Result]:\n----------------------\n{response}\n----------------------");
                await _jsonCommandWriter.WriteAsync(response, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "FATAL: Intent Parser failed.");
        }
    }

    /// <summary>
    /// 封裝 Phi-3.5 推論邏輯。
    /// </summary>
    private async Task<string> RunInferenceAsync(string prompt, string stopToken, CancellationToken ct)
    {
        string result = "";
        try
        {
            await _modelService.Lock.WaitAsync(ct);

            using var generatorParams = new GeneratorParams(_modelService.Model);
            using var sequences = _modelService.Tokenizer.Encode(prompt);
            
            generatorParams.SetSearchOption("max_length", 512);
            generatorParams.SetSearchOption("do_sample", false);
            generatorParams.SetSearchOption("past_present_share_buffer", true);

            using var generator = new Generator(_modelService.Model, generatorParams);
            generator.AppendTokenSequences(sequences);

            using var tokenizerStream = _modelService.Tokenizer.CreateStream();

            while (!generator.IsDone() && !ct.IsCancellationRequested)
            {
                generator.GenerateNextToken();
                var lastTokenId = generator.GetSequence(0)[^1];
                var part = tokenizerStream.Decode(lastTokenId);
                
                if (!string.IsNullOrEmpty(part))
                {
                    result += part;
                    // 偵測到 stopToken 立即切斷
                    if (result.Contains(stopToken)) 
                    {
                        result = result.Substring(0, result.IndexOf(stopToken));
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Inference sub-call failed.");
            return "";
        }
        finally
        {
            _modelService.Lock.Release();
        }
        return result;
    }
}
