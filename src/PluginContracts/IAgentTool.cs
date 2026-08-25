using System.Text.Json;

namespace BackgroundAssistant.PluginContracts;

/// <summary>
/// BackgroundAssistant 與外部 Tool DLL 共用的最小執行契約。
/// 定義外部插件工具必須提供的屬性與執行方法。
/// </summary>
public interface IAgentTool
{
    /// <summary>
    /// 取得工具的描述資訊（包含名稱、說明、參數結構與語音設定）。
    /// </summary>
    ToolDescriptor Descriptor { get; }

    /// <summary>
    /// 非同步執行工具業務邏輯。
    /// </summary>
    /// <param name="arguments">Router 解析傳入的 JSON 參數物件。</param>
    /// <param name="cancellationToken">取消操作的語彙基元。</param>
    /// <returns>回傳包含成功狀態、顯示內容與記憶摘要的 <see cref="ToolResult"/>。</returns>
    Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken);
}
