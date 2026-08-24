using System.ComponentModel;
using System.Diagnostics;

namespace BackgroundAssistant.FileSearch;

public sealed class RipgrepFileSearcher
{
    private readonly FileSearchOptions _options;

    public RipgrepFileSearcher(FileSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.MaxResults <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "MaxResults 必須大於零。");
        }

        if (options.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Timeout 必須大於零。");
        }

        _options = options;
    }

    public async Task<FileSearchOutcome> SearchAsync(
        string fileName,
        CancellationToken cancellationToken)
    {
        ValidateFileName(fileName);

        var roots = _options.SearchRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Path.GetFullPath)
            .Where(Directory.Exists)
            .Distinct(GetPathComparer())
            .ToArray();

        if (roots.Length == 0)
        {
            return new FileSearchOutcome([], FileSearchMatchMode.None, false);
        }

        using var timeoutSource = new CancellationTokenSource(_options.Timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutSource.Token);

        try
        {
            var candidates = await EnumerateCandidatesAsync(
                fileName,
                roots,
                linkedSource.Token);

            var exact = candidates
                .Where(path => string.Equals(
                    Path.GetFileName(path),
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
                .Take(_options.MaxResults)
                .ToArray();

            if (exact.Length > 0)
            {
                return new FileSearchOutcome(exact, FileSearchMatchMode.Exact, false);
            }

            var contains = candidates
                .Where(path => Path.GetFileName(path).Contains(
                    fileName,
                    StringComparison.OrdinalIgnoreCase))
                .Take(_options.MaxResults)
                .ToArray();

            return new FileSearchOutcome(
                contains,
                contains.Length == 0 ? FileSearchMatchMode.None : FileSearchMatchMode.Contains,
                false);
        }
        catch (OperationCanceledException) when (
            timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new FileSearchOutcome([], FileSearchMatchMode.None, true);
        }
    }

    private async Task<IReadOnlyList<string>> EnumerateCandidatesAsync(
        string fileName,
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.RipgrepExecutable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add("--files");
        startInfo.ArgumentList.Add("-uuu");
        startInfo.ArgumentList.Add("--no-config");
        startInfo.ArgumentList.Add("--no-messages");
        startInfo.ArgumentList.Add("--iglob");
        startInfo.ArgumentList.Add($"*{EscapeGlob(fileName)}*");
        startInfo.ArgumentList.Add("--");

        foreach (var root in roots)
        {
            startInfo.ArgumentList.Add(root);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new FileSearchDependencyException("無法啟動 ripgrep。");
            }
        }
        catch (Win32Exception ex)
        {
            throw new FileSearchDependencyException(
                $"找不到或無法啟動 ripgrep：{_options.RipgrepExecutable}",
                ex);
        }

        var standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var candidates = new List<string>();
        var seen = new HashSet<string>(GetPathComparer());

        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var fullPath = Path.IsPathFullyQualified(line)
                    ? Path.GetFullPath(line)
                    : Path.GetFullPath(line, startInfo.WorkingDirectory);

                if (seen.Add(fullPath))
                {
                    candidates.Add(fullPath);
                }
            }

            await process.WaitForExitAsync(cancellationToken);
            var standardError = await standardErrorTask;

            // ripgrep 回傳代碼說明：
            // 0: 找到相符項目
            // 1: 未找到任何項目
            // 2: 發生錯誤（在 Windows 搜尋整顆磁碟時，若遍歷至 System Volume Information 或無權限之系統目錄，rg 會以 code 2 結束）
            // 因此：若有找到候選項，或 stderr 無實質致命錯誤訊息（單純為受保護目錄權限略過），均視為正常完成。
            // 只有在未找到任何候選且 stderr 包含明確錯誤訊息時，才視為執行失敗。
            if (process.ExitCode > 1 && candidates.Count == 0 && !string.IsNullOrWhiteSpace(standardError))
            {
                throw new FileSearchProcessException(
                    $"ripgrep 搜尋失敗，結束代碼：{process.ExitCode}。{standardError.Trim()}");
            }

            return candidates;
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("fileName 不可為空白。", nameof(fileName));
        }

        if (fileName.IndexOfAny(['/', '\\', '\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("fileName 只能包含檔名，不可包含路徑。", nameof(fileName));
        }
    }

    private static string EscapeGlob(string value)
    {
        ReadOnlySpan<char> specialCharacters = ['\\', '*', '?', '[', ']', '{', '}'];
        var result = new System.Text.StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (specialCharacters.Contains(character))
            {
                result.Append('\\');
            }

            result.Append(character);
        }

        return result.ToString();
    }

    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (Win32Exception)
        {
        }
    }
}

public sealed class FileSearchDependencyException : Exception
{
    public FileSearchDependencyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class FileSearchProcessException : Exception
{
    public FileSearchProcessException(string message)
        : base(message)
    {
    }
}
