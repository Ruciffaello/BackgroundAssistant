using System.Text.Json;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 知識庫查詢工具 (模擬)。
/// 用於處理百科全書式、定義類或原理類的詢問。
/// </summary>
public class KnowledgeTools : IMcpTool
{
    /// <summary>
    /// 工具唯一識別名稱。
    /// </summary>
    public string Name => "knowledge_search";

    /// <summary>
    /// 執行知識庫查詢邏輯（目前為模擬回應）。
    /// </summary>
    /// <param name="root">包含 query 關鍵字的 JSON 參數。</param>
    /// <returns>知識庫查詢回應文字。</returns>
    public async Task<string> ExecuteAsync(JsonElement root)
    {
        string query = root.TryGetProperty("query", out var q) ? q.GetString()! : "未知主題";
        
        await Task.Delay(10); // 模擬延遲

        // 目前為模擬回傳，說明這屬於知識庫範疇
        return $"這是一則知識庫查詢。關於「{query}」，目前我的本地數據顯示這是一個深奧的主題，建議您可以進一步查閱維基百科或其他學術資源。";
    }
}
