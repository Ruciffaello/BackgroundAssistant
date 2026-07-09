using System.Text.Json;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 幽默與稱讚工具。
/// </summary>
public class HumorTools : IMcpTool
{
    public string Name => "humor_praise";

    public async Task<string> ExecuteAsync(JsonElement root)
    {
        return "";
    }
}
