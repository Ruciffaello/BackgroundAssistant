using System.ServiceModel.Syndication;
using System.Text.Json;
using System.Xml;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant.Tools;

/// <summary>
/// RSS 新聞查詢工具 (Google News RSS)。
/// 相比 NewsAPI，對中文關鍵字的支援更好且不需 API Key。
/// </summary>
public class RssNewsTools : IMcpTool
{
    private readonly ILogger<RssNewsTools> _logger;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// 工具唯一識別名稱。
    /// </summary>
    public string Name => "rss_news_search";

    /// <summary>
    /// 初始化 <see cref="RssNewsTools"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    public RssNewsTools(ILogger<RssNewsTools> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "BackgroundAssistant/1.0");
    }

    /// <summary>
    /// 透過 Google News RSS 來源搜尋最新即時新聞。
    /// </summary>
    /// <param name="root">包含 query 關鍵字的 JSON 參數。</param>
    /// <returns>格式化後的新聞摘要字串。</returns>
    public async Task<string> ExecuteAsync(JsonElement root)
    {
        string query = root.TryGetProperty("query", out var q) ? q.GetString()! : "";
        
        if (string.IsNullOrWhiteSpace(query))
        {
            return "這是一則 RSS 新聞查詢，但您沒有提供查詢關鍵字。";
        }

        try
        {
            _logger.LogInformation("Searching Google News RSS for: {query}", query);
            
            // Google News RSS URL (Traditional Chinese / Taiwan)
            string rssUrl = $"https://news.google.com/rss/search?q={Uri.EscapeDataString(query)}&hl=zh-TW&gl=TW&ceid=TW:zh-Hant";
            
            using var response = await _httpClient.GetAsync(rssUrl);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = XmlReader.Create(stream);
            var feed = SyndicationFeed.Load(reader);

            if (feed == null || !feed.Items.Any())
            {
                return $"RSS 搜尋目前找不到關於「{query}」的相關新聞。";
            }

            string result = $"透過 RSS 幫您找到了關於「{query}」的最新消息：\n\n";
            int count = 0;
            
            foreach (var item in feed.Items)
            {
                if (count >= 3) break; // 只取前三則

                string title = item.Title.Text;
                
                // Google News 標題通常格式為 "標題 - 來源"，我們稍作清理
                int dashIndex = title.LastIndexOf(" - ");
                if (dashIndex > 0)
                {
                    title = title.Substring(0, dashIndex);
                }

                result += $"第{count + 1}則：{title}。 \n\n";
                count++;
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RSS news search failed");
            return "這是一則 RSS 新聞查詢，但查詢過程中發生了錯誤。";
        }
    }
}
