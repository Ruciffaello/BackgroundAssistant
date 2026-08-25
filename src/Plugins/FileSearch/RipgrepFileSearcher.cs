using System.ComponentModel;
using System.Diagnostics;

namespace BackgroundAssistant.FileSearch;

/// <summary>
/// 封裝 ripgrep (rg) CLI 執行的本機檔案搜尋引擎。
/// 支援 glob 字元轉義、全磁碟遍歷、多執行緒輸出收集、CancellationToken 取消程序樹及 Windows 系統保護目錄容錯。
/// </summary>
public sealed class RipgrepFileSearcher
{
    private readonly FileSearchOptions _options;

    /// <summary>
    /// 初始化 <see cref="RipgrepFileSearcher"/> 的新執行個體。
    /// </summary>
    /// <param name="options">搜尋組態選項。</param>
    /// <exception cref="ArgumentNullException">當 options 為 null 時擲出。</exception>
    /// <exception cref="ArgumentOutOfRangeException">當 MaxResults 或 Timeout 小於等於零時擲出。</exception>
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

    /// <summary>
    /// 非同步執行檔名搜尋。先搜尋完整檔名相符；若無則自動回退至包含檔名相符。
    /// </summary>
    /// <param name="fileName">目標檔案名稱（不可包含目錄分隔符號）。</param>
    /// <param name="cancellationToken">取消操作語彙基元。</param>
    /// <returns>回傳包含搜尋結果路徑、比對模式與逾時狀態的 <see cref="FileSearchOutcome"/>。</returns>
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

    /// <summary>
    /// 啟動 ripgrep 外部程序並列舉相符的檔案路徑清單。
    /// </summary>
    /// <param name="fileName">搜尋檔名。</param>
    /// <param name="roots">搜尋根目錄陣列。</param>
    /// <param name="cancellationToken">取消語彙基元。</param>
    /// <returns>相符檔案完整路徑清單。</returns>
    /// <exception cref="FileSearchDependencyException">當找不到或無法啟動 rg 執行檔時擲出。</exception>
    /// <exception cref="FileSearchProcessException">當 rg 執行失敗且有明確錯誤時擲出。</exception>
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

    /// <summary>
    /// 驗證檔名參數合法性，確保不為空白且不包含路徑周遊分隔符號。
    /// </summary>
    /// <param name="fileName">檔名輸入字串。</param>
    /// <exception cref="ArgumentException">當檔名為空或包含路徑字元時擲出。</exception>
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

    /// <summary>
    /// 將檔名中的 Glob 特殊字元（如 *、?、[ 等）進行轉義，確保依字面搜尋。
    /// </summary>
    /// <param name="value">原始檔名字串。</param>
    /// <returns>轉義後的 Glob 字串。</returns>
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

    /// <summary>
    /// 依作業系統取得適當的路徑字串比較器。
    /// </summary>
    /// <returns>Windows/macOS 為忽略大小寫比較器，Linux 為區分大小寫比較器。</returns>
    private static StringComparer GetPathComparer() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>
    /// 嘗試強制結束執行中的外部程序及其整個程序樹。
    /// </summary>
    /// <param name="process">目標 Process 物件。</param>
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

/// <summary>
/// 當缺少 ripgrep (rg) 外部相依執行檔時擲出的例外。
/// </summary>
public sealed class FileSearchDependencyException : Exception
{
    /// <summary>
    /// 初始化 <see cref="FileSearchDependencyException"/> 的新執行個體。
    /// </summary>
    /// <param name="message">錯誤訊息。</param>
    /// <param name="innerException">內部例外。</param>
    public FileSearchDependencyException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// 當 ripgrep 外部程序執行發生致命錯誤時擲出的例外。
/// </summary>
public sealed class FileSearchProcessException : Exception
{
    /// <summary>
    /// 初始化 <see cref="FileSearchProcessException"/> 的新執行個體。
    /// </summary>
    /// <param name="message">錯誤訊息。</param>
    public FileSearchProcessException(string message)
        : base(message)
    {
    }
}
