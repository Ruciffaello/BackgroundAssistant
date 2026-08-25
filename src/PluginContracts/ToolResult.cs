namespace BackgroundAssistant.PluginContracts;

/// <summary>
/// Tool 的執行結果資料記錄。
/// 封裝介面顯示結果、對話歷史記憶摘要與可辨識的錯誤代碼。
/// </summary>
/// <param name="Success">工具執行是否成功。</param>
/// <param name="Content">顯示給使用者的完整執行結果文字內容。</param>
/// <param name="MemorySummary">保存至最近對話歷史的精簡摘要（若為 null 則使用 Content；長文本建議提供摘要以節省 token）。</param>
/// <param name="ErrorCode">發生錯誤時的唯一錯誤識別代碼（例如 ripgrep_unavailable、invalid_file_name 等）。</param>
public sealed record ToolResult(
    bool Success,
    string Content,
    string? MemorySummary = null,
    string? ErrorCode = null);
