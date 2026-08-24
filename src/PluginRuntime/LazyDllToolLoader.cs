using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using BackgroundAssistant.PluginContracts;

namespace BackgroundAssistant.PluginRuntime;

public sealed record DllToolExecution(
    ToolResult Result,
    bool SpeakResult,
    bool LoadedNewVersion,
    string? ReloadWarning = null);

public sealed class LazyDllToolLoader : IAsyncDisposable
{
    private readonly ToolManifestCatalog _catalog;
    private readonly string _cacheRootDirectory;
    private readonly ConcurrentDictionary<string, ToolSlot> _slots = new(StringComparer.Ordinal);

    public LazyDllToolLoader(ToolManifestCatalog catalog, string cacheRootDirectory)
    {
        _catalog = catalog;
        _cacheRootDirectory = Path.GetFullPath(cacheRootDirectory);
    }

    public bool IsLoaded(string toolName) =>
        _slots.TryGetValue(toolName, out var slot) && slot.Loaded is not null;

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

    private static bool IsRecoverableLoadFailure(Exception exception) => exception is
        PluginLoadException or
        BadImageFormatException or
        FileLoadException or
        FileNotFoundException or
        IOException or
        UnauthorizedAccessException or
        TypeLoadException or
        TargetInvocationException;

    private sealed class ToolSlot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public LoadedTool? Loaded { get; set; }
    }

    private sealed record LoadedTool(
        string Fingerprint,
        IAgentTool Tool,
        PluginAssemblyLoadContext LoadContext);
}
