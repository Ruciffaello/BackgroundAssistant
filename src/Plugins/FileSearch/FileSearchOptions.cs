namespace BackgroundAssistant.FileSearch;

/// <summary>
/// 檔案搜尋工具的執行組態選項。
/// </summary>
public sealed class FileSearchOptions
{
    /// <summary>
    /// ripgrep 執行檔名稱或完整路徑（預設為 "rg"）。
    /// </summary>
    public string RipgrepExecutable { get; init; } = "rg";

    /// <summary>
    /// 搜尋的根目錄集合（預設為系統所有就緒的本機磁碟機）。
    /// </summary>
    public IReadOnlyList<string> SearchRoots { get; init; } = GetDefaultSearchRoots();

    /// <summary>
    /// 搜尋逾時時間（預設為 15 秒）。
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 最多回傳的檔案數量上限（預設為 20 筆）。
    /// </summary>
    public int MaxResults { get; init; } = 20;

    /// <summary>
    /// 取得作業系統預設的搜尋根目錄清單。
    /// </summary>
    /// <returns>Windows 為本機與抽取式磁碟根目錄，其餘為系統根目錄。</returns>
    private static IReadOnlyList<string> GetDefaultSearchRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives()
                .Where(drive => drive.IsReady)
                .Where(drive => drive.DriveType is DriveType.Fixed or DriveType.Removable)
                .Select(drive => drive.RootDirectory.FullName)
                .ToArray();
        }

        return [Path.GetPathRoot(Environment.CurrentDirectory) ?? "/"];
    }
}
