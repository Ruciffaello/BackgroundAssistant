using System.Text.Json;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 寶可夢卡牌搜尋工具 (範例實作)。
/// 示範如何解析 JSON 參數並回傳模擬的搜尋結果。
/// </summary>
public class PtcgTools : IMcpTool
{
    /// <summary>
    /// 工具唯一識別名稱。
    /// </summary>
    public string Name => "ptcg_search";

    /// <summary>
    /// 執行寶可夢卡牌資訊搜尋（目前為模擬回應）。
    /// </summary>
    /// <param name="root">包含 query 卡牌名稱的 JSON 參數。</param>
    /// <returns>卡牌資訊描述文字。</returns>
    public async Task<string> ExecuteAsync(JsonElement root)
    {
        // 嘗試從 JSON 提取 query 參數
        string query = root.TryGetProperty("query", out var q) ? q.GetString()! : "未知卡牌";
        
        await Task.Delay(10); // 模擬非同步操作
        
        // 目前為模擬回傳，未來可對接真正的 API
        return $"我幫您找到了關於「{query}」的卡牌資訊，這張卡目前在市場上非常熱門。";
    }
}
