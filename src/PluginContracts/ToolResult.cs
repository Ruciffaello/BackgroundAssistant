namespace BackgroundAssistant.PluginContracts;

/// <summary>
/// Tool 的顯示結果、記憶摘要與可辨識錯誤。
/// </summary>
public sealed record ToolResult(
    bool Success,
    string Content,
    string? MemorySummary = null,
    string? ErrorCode = null);
