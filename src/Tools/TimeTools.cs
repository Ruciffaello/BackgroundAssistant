using System.Text.Json;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 世界時間查詢工具。
/// 支援查詢指定地區的目前時間，或回傳本地時間。
/// </summary>
public class TimeTools : IMcpTool
{
    /// <summary>
    /// 工具唯一識別名稱。
    /// </summary>
    public string Name => "get_time";

    /// <summary>
    /// 依據指定的地區或本地時區，計算並格式化當前時間文字。
    /// </summary>
    /// <param name="root">包含 location 參數的 JSON 元素。</param>
    /// <returns>格式化的時間播報字串。</returns>
    public async Task<string> ExecuteAsync(JsonElement root)
    {
        // 取得地點參數
        string location = root.TryGetProperty("location", out var l) ? l.GetString()! : "Local";
        
        await Task.Delay(10);

        var now = DateTime.Now;
        string locLower = location.ToLower();

        // 判斷邏輯：優先匹配英文標識
        if (locLower.Contains("tokyo") || locLower.Contains("japan") || locLower.Contains("東京"))
        {
            var jst = now.AddHours(1);
            return $"日本東京現在的時間是 {jst:HH} 點 {jst:mm} 分。";
        }

        if (locLower.Contains("new york") || locLower.Contains("nyc") || locLower.Contains("紐約"))
        {
            var est = now.AddHours(-12);
            return $"美國紐約現在的時間是 {est:HH} 點 {est:mm} 分。";
        }

        if (locLower.Contains("london") || locLower.Contains("倫敦"))
        {
            var bst = now.AddHours(-7);
            return $"英國倫敦現在的時間是 {bst:HH} 點 {bst:mm} 分。";
        }

        return $"現在的時間是 {now:HH} 點 {now:mm} 分。";
    }
}
