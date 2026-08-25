namespace BackgroundAssistant.FileSearch;

/// <summary>
/// 檔案搜尋成果記錄。
/// </summary>
/// <param name="Paths">找到的相符檔案路徑清單。</param>
/// <param name="MatchMode">匹配模式（完整相符或包含相符）。</param>
/// <param name="TimedOut">搜尋是否因逾時而中斷。</param>
public sealed record FileSearchOutcome(
    IReadOnlyList<string> Paths,
    FileSearchMatchMode MatchMode,
    bool TimedOut);

/// <summary>
/// 檔案名稱匹配模式。
/// </summary>
public enum FileSearchMatchMode
{
    /// <summary>
    /// 未找到任何相符檔案。
    /// </summary>
    None,

    /// <summary>
    /// 完整檔名完全相同（不分大小寫）。
    /// </summary>
    Exact,

    /// <summary>
    /// 檔名包含搜尋關鍵字（不分大小寫）。
    /// </summary>
    Contains
}
