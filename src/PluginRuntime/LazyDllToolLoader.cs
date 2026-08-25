using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using BackgroundAssistant.PluginContracts;

namespace BackgroundAssistant.PluginRuntime;

/// <summary>
/// DLL 插件工具執行成果記錄。
/// </summary>
/// <param name="Result">工具執行回傳的 <see cref="ToolResult"/>。</param>
/// <param name="SpeakResult">執行結果是否需送入 TTS 朗讀。</param>
/// <param name="LoadedNewVersion">此次呼叫是否載入了新版本的 DLL。</param>
/// <param name="ReloadWarning">若新版 DLL 載入失敗而回退至舊版時的警告訊息。</param>
public sealed record DllToolExecution(
    ToolResult Result,
    bool SpeakResult,
    bool LoadedNewVersion,
    string? ReloadWarning = null);

/// <summary>
/// 延遲（按需）載入 DLL 工具的載入器與執行器。
/// 支援 SHA-256 雜湊指紋比對、影子副本防檔案鎖定、可回收 ALC 隔離以及損壞回退機制。
/// </summary>
public sealed class LazyDllToolLoader : IAsyncDisposable
{
    private readonly ToolManifestCatalog _catalog;
    private readonly string _cacheRootDirectory;
    private readonly ConcurrentDictionary<string, ToolSlot> _slots = new(StringComparer.Ordinal);

    /// <summary>
    /// 初始化 <see cref="LazyDllToolLoader"/> 的新執行個體。
    /// </summary>
    /// <param name="catalog">插件資訊清單目錄管理員。</param>
    /// <param name="cacheRootDirectory">用於存放 DLL 影子副本快取的根目錄路徑。</param>
    public LazyDllToolLoader(ToolManifestCatalog catalog, string cacheRootDirectory)
    {
        _catalog = catalog;
        _cacheRootDirectory = Path.GetFullPath(cacheRootDirectory);
    }

    /// <summary>
    /// 檢查指定的工具目前是否已經被載入至記憶體中。
    /// </summary>
    /// <param name="toolName">工具唯一名稱。</param>
    /// <returns>若已載入實例則回傳 true，否則為 false。</returns>
    public bool IsLoaded(string toolName) =>
        _slots.TryGetValue(toolName, out var slot) && slot.Loaded is not null;

    /// <summary>
    /// 非同步執行指定的 DLL 工具。若尚未載入或 DLL 檔案已變更，會自動進行指紋比對並按需載入。
    /// </summary>
    /// <param name="toolName">工具唯一名稱。</param>
    /// <param name="arguments">Router 解析的 JSON 參數。</param>
    /// <param name="cancellationToken">取消操作的語彙基元。</param>
    /// <returns>包含執行結果與版本資訊的 <see cref="DllToolExecution"/> 物件。</returns>
    /// <exception cref="PluginLoadException">當找不到工具或首次載入失敗時擲出。</exception>
    public async Task<DllToolExecution> ExecuteAsync(
        string toolName,
        System.Text.Json.JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!_catalog.TryGetTool(toolName, out var registration))
        {
            throw new PluginLoadException("plugin_not_found", $"找不到 DLL Tool：{toolName}");
        }

        var slot = _slots.GetOrAdd(toolName, _ => new ToolSlot());
        await slot.Gate.WaitAsync(cancellationToken);

        try
        {
            var fingerprint = await ComputeFingerprintAsync(
                registration.EntryAssemblyPath,
                cancellationToken);
            var loadedNewVersion = false;
            string? reloadWarning = null;

            if (slot.Loaded is null || !string.Equals(slot.Loaded.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                try
                {
                    var replacement = LoadTool(registration, fingerprint);
                    var previous = slot.Loaded;
                    slot.Loaded = replacement;
                    loadedNewVersion = true;
                    previous?.LoadContext.Unload();
                }
                catch (Exception ex) when (IsRecoverableLoadFailure(ex))
                {
                    if (slot.Loaded is null)
                    {
                        throw new PluginLoadException(
                            "tool_load_failed",
                            $"無法載入 DLL Tool {toolName}：{ex.Message}",
                            ex);
                    }

                    reloadWarning = $"新版 DLL 載入失敗，繼續使用先前版本：{ex.Message}";
                }
            }

            if (slot.Loaded is null)
            {
                throw new PluginLoadException(
                    "tool_load_failed",
                    $"無法載入 DLL Tool：{toolName}");
            }

            ToolResult result;
            try
            {
                result = await slot.Loaded.Tool.ExecuteAsync(arguments, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                result = new ToolResult(
                    false,
                    $"工具 {toolName} 執行失敗：{ex.Message}",
                    $"工具 {toolName} 執行失敗。",
                    "tool_execution_failed");
            }

            return new DllToolExecution(
                result,
                slot.Loaded.Tool.Descriptor.SpeakResult,
                loadedNewVersion,
                reloadWarning);
        }
        finally
        {
            slot.Gate.Release();
        }
    }

    /// <summary>
    /// 非同步釋放所有已載入之插件 ALC 與並行控制鎖。
    /// </summary>
    public ValueTask DisposeAsync()
    {
        foreach (var slot in _slots.Values)
        {
            slot.Loaded?.LoadContext.Unload();
            slot.Gate.Dispose();
        }

        _slots.Clear();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 透過影子副本與 Reflection 實體化目標 Tool 組件。
    /// </summary>
    /// <param name="registration">插件註冊資訊。</param>
    /// <param name="fingerprint">來源 DLL 的 SHA-256 雜湊指紋。</param>
    /// <returns>已載入並驗證完成的 <see cref="LoadedTool"/> 物件。</returns>
    private LoadedTool LoadTool(ToolManifestRegistration registration, string fingerprint)
    {
        var cacheDirectory = Path.Combine(
            _cacheRootDirectory,
            registration.Manifest.Id,
            fingerprint);
        Directory.CreateDirectory(cacheDirectory);

        var cachedAssemblyPath = Path.Combine(
            cacheDirectory,
            Path.GetFileName(registration.EntryAssemblyPath));
        CopyAtomicallyIfMissing(registration.EntryAssemblyPath, cachedAssemblyPath);

        var loadContext = new PluginAssemblyLoadContext(cachedAssemblyPath);
        try
        {
            using var assemblyStream = new FileStream(
                cachedAssemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            var assembly = loadContext.LoadFromStream(assemblyStream);
            var entryType = assembly.GetType(
                registration.Manifest.EntryType,
                throwOnError: false,
                ignoreCase: false) ?? throw new PluginLoadException(
                    "entry_type_not_found",
                    $"找不到入口型別：{registration.Manifest.EntryType}");

            if (!typeof(IAgentTool).IsAssignableFrom(entryType))
            {
                throw new PluginLoadException(
                    "contract_type_mismatch",
                    $"入口型別沒有實作 {nameof(IAgentTool)}：{registration.Manifest.EntryType}");
            }

            if (entryType.IsAbstract || entryType.GetConstructor(Type.EmptyTypes) is null)
            {
                throw new PluginLoadException(
                    "invalid_entry_type",
                    "入口型別必須是非抽象類別，並具有公開無參數建構函式。");
            }

            var tool = (IAgentTool)(Activator.CreateInstance(entryType)
                ?? throw new PluginLoadException("tool_load_failed", "無法建立 Tool 實例。"));

            if (!string.Equals(tool.Descriptor.Name, registration.Manifest.Id, StringComparison.Ordinal))
            {
                throw new PluginLoadException(
                    "tool_name_mismatch",
                    $"Tool 名稱 {tool.Descriptor.Name} 與 manifest id {registration.Manifest.Id} 不一致。");
            }

            if (tool.Descriptor.SpeakResult != registration.Manifest.SpeakResult)
            {
                throw new PluginLoadException(
                    "output_policy_mismatch",
                    "Tool 的 SpeakResult 與 manifest 不一致。");
            }

            return new LoadedTool(fingerprint, tool, loadContext);
        }
        catch
        {
            loadContext.Unload();
            throw;
        }
    }

    /// <summary>
    /// 計算來源組件檔案的 SHA-256 雜湊值作為指紋。
    /// </summary>
    /// <param name="assemblyPath">組件 DLL 檔案路徑。</param>
    /// <param name="cancellationToken">取消操作語彙基元。</param>
    /// <returns>16 進位字串格式的 SHA-256 雜湊值。</returns>
    private static async Task<string> ComputeFingerprintAsync(
        string assemblyPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                assemblyPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 81920,
                useAsync: true);
            var hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return Convert.ToHexString(hash);
        }
        catch (FileNotFoundException ex)
        {
            throw new PluginLoadException("dll_not_found", $"找不到 Tool DLL：{assemblyPath}", ex);
        }
        catch (IOException ex)
        {
            throw new PluginLoadException("dll_read_failed", $"無法讀取 Tool DLL：{assemblyPath}", ex);
        }
    }

    /// <summary>
    /// 以不可部分完成 (Atomic) 的方式將來源 DLL 複製至影子快取目錄（若快取檔案已存在則直接略過）。
    /// </summary>
    /// <param name="sourcePath">來源 DLL 路徑。</param>
    /// <param name="destinationPath">快取目標路徑。</param>
    private static void CopyAtomicallyIfMissing(string sourcePath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            return;
        }

        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.Copy(sourcePath, temporaryPath, overwrite: false);
            File.Move(temporaryPath, destinationPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(destinationPath))
        {
            // 另一個載入動作已完成相同指紋的影子複製。
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// 判斷例外是否屬於可復原的載入失敗（若可復原，將嘗試回退至舊版已載入實例）。
    /// </summary>
    /// <param name="exception">發生的例外物件。</param>
    /// <returns>若為可復原錯誤則回傳 true，否則為 false。</returns>
    private static bool IsRecoverableLoadFailure(Exception exception) => exception is
        PluginLoadException or
        BadImageFormatException or
        FileLoadException or
        FileNotFoundException or
        IOException or
        UnauthorizedAccessException or
        TypeLoadException or
        TargetInvocationException;

    /// <summary>
    /// 單一工具的插槽狀態，包含並行控制訊號量與當前載入實例。
    /// </summary>
    private sealed class ToolSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public LoadedTool? Loaded { get; set; }
    }

    /// <summary>
    /// 已載入插件的封裝資料，包含指紋、Tool 實例與其所屬的 AssemblyLoadContext。
    /// </summary>
    private sealed record LoadedTool(
        string Fingerprint,
        IAgentTool Tool,
        PluginAssemblyLoadContext LoadContext);
}
