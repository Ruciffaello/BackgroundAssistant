namespace BackgroundAssistant.FileSearch;

public sealed class FileSearchOptions
{
    public string RipgrepExecutable { get; init; } = "rg";

    public IReadOnlyList<string> SearchRoots { get; init; } = GetDefaultSearchRoots();

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public int MaxResults { get; init; } = 20;

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
