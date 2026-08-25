using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 新聞查詢工具。
/// 採用兩階段搜尋策略，並使用 qInTitle 確保關鍵字出現在標題中，提升準確度。
/// </summary>
public class NewsTools : IMcpTool
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<NewsTools> _logger;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 工具唯一識別名稱。
    /// </summary>
    public string Name => "news_search";

    /// <summary>
    /// 初始化 <see cref="NewsTools"/> 的新執行個體。
    /// </summary>
    /// <param name="configuration">應用程式組態。</param>
    /// <param name="logger">記錄器實例。</param>
    public NewsTools(IConfiguration configuration, ILogger<NewsTools> logger)
    {
        _configuration = configuration;
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "BackgroundAssistant/1.0");
    }

    /// <summary>
    /// 透過 NewsAPI 非同步執行新聞搜尋（先頭條後全網）。
    /// </summary>
    /// <param name="root">包含 query 關鍵字的 JSON 參數。</param>
    /// <returns>格式化後的新聞標題摘要字串。</returns>
    public async Task<string> ExecuteAsync(JsonElement root)
    {
        string query = root.TryGetProperty("query", out var q) ? q.GetString()! : "";
        
        if (string.IsNullOrWhiteSpace(query))
        {
            return "這是一則新聞查詢，但您沒有提供查詢關鍵字。";
        }

        string apiKey = _configuration["NewsApi:ApiKey"] ?? "";
        if (string.IsNullOrEmpty(apiKey) || apiKey == "YOUR_NEWS_API_KEY_HERE")
        {
            return "這是一則新聞查詢，但系統尚未設定 NewsAPI 金鑰。";
        }

        try
        {
            // --- 第一階段：查詢台灣頭條 (使用 qInTitle 確保相關性) ---
            _logger.LogInformation("Stage 1: Searching top headlines (in title) for: {query}", query);
            // 注意：top-headlines 不支援 qInTitle 參數，必須用 q，但它本來就只搜標題為主
            string topHeadlinesUrl = $"https://newsapi.org/v2/top-headlines?q={Uri.EscapeDataString(query)}&country=tw&pageSize=3&apiKey={apiKey}";
            
            var response = await _httpClient.GetAsync(topHeadlinesUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var articles = doc.RootElement.GetProperty("articles");

                if (articles.GetArrayLength() > 0)
                {
                    return FormatResult(query, articles, "今日頭條");
                }
            }

            // --- 第二階段：全網搜尋 (關鍵：使用 qInTitle 排除內文雜訊) ---
            _logger.LogInformation("Stage 2: Falling back to everything search (qInTitle) for: {query}", query);
            string everythingUrl = $"https://newsapi.org/v2/everything?qInTitle={Uri.EscapeDataString(query)}&sortBy=publishedAt&pageSize=3&language=zh&apiKey={apiKey}";
            
            response = await _httpClient.GetAsync(everythingUrl);
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(content);
                var articles = doc.RootElement.GetProperty("articles");

                if (articles.GetArrayLength() > 0)
                {
                    return FormatResult(query, articles, "全網最新消息");
                }
            }

            return $"這是一則新聞查詢，目前找不到標題包含「{query}」的相關新聞。";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "News search failed");
            return "這是一則新聞查詢，但查詢過程中發生了預料之外的錯誤。";
        }
    }

    /// <summary>
    /// 將 NewsAPI 回傳的文章列表格式化為易於播報的文字摘要。
    /// </summary>
    /// <param name="query">搜尋關鍵字。</param>
    /// <param name="articles">新聞文章 JSON 陣列。</param>
    /// <param name="sourceTag">來源標籤（今日頭條/全網最新消息）。</param>
    /// <returns>格式化文字。</returns>
    private string FormatResult(string query, JsonElement articles, string sourceTag)
    {
        string result = $"幫您找到了關於「{query}」的{sourceTag}：\n\n";
        int count = 0;
        foreach (var article in articles.EnumerateArray())
        {
            if (count >= 2) break;
            string title = article.GetProperty("title").GetString() ?? "無標題";
            int dashIndex = title.LastIndexOf(" - ");
            if (dashIndex > 0) title = title.Substring(0, dashIndex);
            
            result += $"第{count + 1}則：{title}。 \n\n";
            count++;
        }
        return result;
    }
}
