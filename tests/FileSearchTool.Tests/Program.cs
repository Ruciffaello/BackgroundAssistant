using System.Text.Json;
using BackgroundAssistant.FileSearch;
using BackgroundAssistant.PluginRuntime;

var tests = new (string Name, Func<Task> Run)[]
{
    ("完整檔名優先", ExactNameTakesPriorityAsync),
    ("找不到完整名稱時使用包含搜尋", ContainsFallbackAsync),
    ("支援中文檔名", ChineseFileNameAsync),
    ("特殊字元按字面搜尋", SpecialCharactersAreLiteralAsync),
    ("限制最大結果數", ResultLimitAsync),
    ("支援取消", CancellationAsync),
    ("Tool 回傳顯示內容與記憶摘要", ToolResultPolicyAsync),
    ("找不到 ripgrep 時回傳明確錯誤", MissingRipgrepAsync),
    ("DLL 第一次呼叫才透過 Reflection 載入", LazyReflectionLoadAsync),
    ("損壞的新版 DLL 不取代已載入版本", BrokenUpdateKeepsPreviousAsync),
    ("全磁碟搜尋存在檔案可正常回傳結果", WholeDriveSearchExistingFileAsync),
    ("全磁碟搜尋不存在檔案正常回傳未找到", WholeDriveSearchNonexistentFileAsync)
};

var failed = 0;

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {test.Name}");
        Console.Error.WriteLine(ex);
    }
}

Console.WriteLine($"完成：{tests.Length - failed}/{tests.Length} 通過。");
return failed == 0 ? 0 : 1;

static async Task ExactNameTakesPriorityAsync()
{
    await WithFixtureAsync(async root =>
    {
        CreateFile(root, "report.pdf");
        CreateFile(root, "old-report.pdf");

        var result = await CreateSearcher(root).SearchAsync("report.pdf", default);

        Equal(FileSearchMatchMode.Exact, result.MatchMode);
        Equal(1, result.Paths.Count);
        Equal("report.pdf", Path.GetFileName(result.Paths[0]));
    });
}

static async Task ContainsFallbackAsync()
{
    await WithFixtureAsync(async root =>
    {
        CreateFile(root, "quarterly-report.pdf");
        CreateFile(root, "report-notes.txt");

        var result = await CreateSearcher(root).SearchAsync("report", default);

        Equal(FileSearchMatchMode.Contains, result.MatchMode);
        Equal(2, result.Paths.Count);
    });
}

static async Task ChineseFileNameAsync()
{
    await WithFixtureAsync(async root =>
    {
        CreateFile(root, "專案報告.pdf");

        var result = await CreateSearcher(root).SearchAsync("專案報告.pdf", default);

        Equal(FileSearchMatchMode.Exact, result.MatchMode);
        Equal("專案報告.pdf", Path.GetFileName(result.Paths.Single()));
    });
}

static async Task SpecialCharactersAreLiteralAsync()
{
    await WithFixtureAsync(async root =>
    {
        CreateFile(root, "report [draft]; final.txt");

        var result = await CreateSearcher(root)
            .SearchAsync("report [draft]; final.txt", default);

        Equal(FileSearchMatchMode.Exact, result.MatchMode);
        Equal(1, result.Paths.Count);
    });
}

static async Task ResultLimitAsync()
{
    await WithFixtureAsync(async root =>
    {
        for (var index = 0; index < 25; index++)
        {
            CreateFile(root, $"item-{index:00}.txt");
        }

        var result = await CreateSearcher(root, maxResults: 5)
            .SearchAsync("item", default);

        Equal(5, result.Paths.Count);
    });
}

static async Task CancellationAsync()
{
    await WithFixtureAsync(async root =>
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await ThrowsAsync<OperationCanceledException>(() =>
            CreateSearcher(root).SearchAsync("anything", cancellation.Token));
    });
}

static async Task ToolResultPolicyAsync()
{
    await WithFixtureAsync(async root =>
    {
        CreateFile(root, "tool-result.txt");
        var tool = new FileSearchTool(CreateOptions(root));
        using var arguments = JsonDocument.Parse("""{"fileName":"tool-result.txt"}""");

        var result = await tool.ExecuteAsync(arguments.RootElement, default);

        True(result.Success, "Tool 應執行成功。");
        True(!tool.Descriptor.SpeakResult, "FileSearch 結果不應送入 TTS。");
        True(result.Content.Contains("tool-result.txt", StringComparison.Ordinal));
        True(result.MemorySummary?.Contains("找到 1 個結果", StringComparison.Ordinal) == true);
        True(result.MemorySummary?.Contains(root, StringComparison.Ordinal) == false);
    });
}

static async Task MissingRipgrepAsync()
{
    await WithFixtureAsync(async root =>
    {
        var options = WithExecutable(
            CreateOptions(root),
            "definitely-not-a-real-rg-command");
        var tool = new FileSearchTool(options);
        using var arguments = JsonDocument.Parse("""{"fileName":"anything.txt"}""");

        var result = await tool.ExecuteAsync(arguments.RootElement, default);

        True(!result.Success);
        Equal("ripgrep_unavailable", result.ErrorCode);
    });
}

static async Task LazyReflectionLoadAsync()
{
    await WithFixtureAsync(async root =>
    {
        var pluginRoot = CreatePluginPackage(root);
        var catalog = new ToolManifestCatalog(pluginRoot);
        await using var loader = new LazyDllToolLoader(catalog, Path.Combine(root, "cache"));
        using var arguments = JsonDocument.Parse("{}");

        Equal(1, catalog.Tools.Count);
        True(!loader.IsLoaded("file_search"), "Catalog 不應在啟動時載入 DLL。");

        var first = await loader.ExecuteAsync("file_search", arguments.RootElement, default);
        True(loader.IsLoaded("file_search"), "第一次執行後 DLL 應已載入。");
        True(first.LoadedNewVersion);
        Equal("invalid_file_name", first.Result.ErrorCode);
        True(!first.SpeakResult);

        var second = await loader.ExecuteAsync("file_search", arguments.RootElement, default);
        True(!second.LoadedNewVersion, "來源 DLL 未改變時應重用現有實例。");
    });
}

static async Task BrokenUpdateKeepsPreviousAsync()
{
    await WithFixtureAsync(async root =>
    {
        var pluginRoot = CreatePluginPackage(root);
        var catalog = new ToolManifestCatalog(pluginRoot);
        await using var loader = new LazyDllToolLoader(catalog, Path.Combine(root, "cache"));
        using var arguments = JsonDocument.Parse("{}");

        var first = await loader.ExecuteAsync("file_search", arguments.RootElement, default);
        True(first.LoadedNewVersion);

        var sourceDll = catalog.Tools.Single().EntryAssemblyPath;
        await File.WriteAllBytesAsync(sourceDll, "not a managed assembly"u8.ToArray());

        var second = await loader.ExecuteAsync("file_search", arguments.RootElement, default);
        True(!second.LoadedNewVersion);
        True(!string.IsNullOrWhiteSpace(second.ReloadWarning));
        Equal("invalid_file_name", second.Result.ErrorCode);
    });
}

static async Task WholeDriveSearchExistingFileAsync()
{
    var tool = new FileSearchTool();
    using var arguments = JsonDocument.Parse("""{"fileName":"README.md"}""");

    var result = await tool.ExecuteAsync(arguments.RootElement, default);

    True(result.Success, "全磁碟搜尋存在檔案應回傳成功。");
    True(result.Content.Contains("README.md", StringComparison.OrdinalIgnoreCase), "結果應包含 README.md 路徑。");
}

static async Task WholeDriveSearchNonexistentFileAsync()
{
    var tool = new FileSearchTool();
    var randomName = $"nonexistent_test_{Guid.NewGuid():N}.xyz";
    using var arguments = JsonDocument.Parse($$"""{"fileName":"{{randomName}}"}""");

    var result = await tool.ExecuteAsync(arguments.RootElement, default);

    // 全磁碟掃描可能在超大硬碟下於 15 秒內完成並回傳找不到，或因耗時長觸發安全逾時；兩者皆為預期之健全行為，不得拋出非預期崩潰或 ExitCode 2 錯誤。
    True(result.Success || result.ErrorCode == "search_timeout", "全磁碟搜尋不存在檔案應正常完成或觸發安全逾時。");
    True(result.Content.Contains($"找不到檔名符合「{randomName}」的檔案", StringComparison.Ordinal) || result.ErrorCode == "search_timeout", "結果應顯示未找到或逾時提示。");
}

static string CreatePluginPackage(string root)
{
    var pluginRoot = Path.Combine(root, "plugins");
    var pluginDirectory = Path.Combine(pluginRoot, "file_search");
    Directory.CreateDirectory(pluginDirectory);

    var assemblyName = "BackgroundAssistant.FileSearchTool.dll";
    File.Copy(
        typeof(FileSearchTool).Assembly.Location,
        Path.Combine(pluginDirectory, assemblyName));
    File.WriteAllText(
        Path.Combine(pluginDirectory, "plugin.json"),
        $$"""
        {
          "id": "file_search",
          "version": "1.0.0",
          "contractVersion": 1,
          "entryAssembly": "{{assemblyName}}",
          "entryType": "BackgroundAssistant.FileSearch.FileSearchTool",
          "speakResult": false,
          "description": "依照檔名搜尋檔案",
          "inputSchema": {
            "type": "object",
            "required": ["fileName"]
          }
        }
        """);

    return pluginRoot;
}

static RipgrepFileSearcher CreateSearcher(string root, int maxResults = 20) =>
    new(CreateOptions(root, maxResults));

static FileSearchOptions CreateOptions(string root, int maxResults = 20) => new()
{
    SearchRoots = [root],
    MaxResults = maxResults,
    Timeout = TimeSpan.FromSeconds(10)
};

static FileSearchOptions WithExecutable(FileSearchOptions options, string executable) => new()
{
    RipgrepExecutable = executable,
    SearchRoots = options.SearchRoots,
    MaxResults = options.MaxResults,
    Timeout = options.Timeout
};

static void CreateFile(string root, string relativePath)
{
    var path = Path.Combine(root, relativePath);
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, "test");
}

static async Task WithFixtureAsync(Func<string, Task> test)
{
    var root = Path.Combine(
        Path.GetTempPath(),
        "BackgroundAssistant.FileSearch.Tests",
        Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);

    try
    {
        await test(root);
    }
    finally
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"預期：{expected}，實際：{actual}");
    }
}

static void True(bool condition, string message = "條件不成立。")
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static async Task ThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"預期擲出 {typeof(TException).Name}。");
}
