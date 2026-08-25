using System.Text.Json;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 幽默與稱讚工具。
/// </summary>
public class HumorTools : IMcpTool
{
    /// <summary>
    /// 工具唯一識別名稱。
    /// </summary>
    public string Name => "humor_praise";

    /// <summary>
    /// 執行幽默稱讚工具邏輯。
    /// </summary>
    /// <param name="root">JSON 參數元素。</param>
    /// <returns>回應文字。</returns>
    public async Task<string> ExecuteAsync(JsonElement root)
    {
        return "";
    }
}
