using System.Reflection;
using System.Runtime.Loader;
using BackgroundAssistant.PluginContracts;

namespace BackgroundAssistant.PluginRuntime;

/// <summary>
/// 插件專屬的組件載入內容 (AssemblyLoadContext)。
/// 支援可回收 (Collectible) 特性，提供獨立組件依賴解析，並確保與 Host 共用 PluginContracts 抽象契約。
/// </summary>
internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ContractAssembly = typeof(IAgentTool).Assembly;
    private static readonly string ContractAssemblyName = ContractAssembly.GetName().Name!;
    private readonly AssemblyDependencyResolver _resolver;

    /// <summary>
    /// 初始化 <see cref="PluginAssemblyLoadContext"/> 的新執行個體。
    /// </summary>
    /// <param name="mainAssemblyPath">插件主要入口 DLL 的完整路徑，用於初始化依賴解析器。</param>
    public PluginAssemblyLoadContext(string mainAssemblyPath)
        : base($"Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    /// <summary>
    /// 解析並載入 Managed 組件。共用契約固定由 Host 載入，其餘組件依賴本機目錄解析。
    /// </summary>
    /// <param name="assemblyName">要載入的組件名稱。</param>
    /// <returns>載入的 Assembly 實例，或 null 由預設上下文載入。</returns>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractAssemblyName, StringComparison.Ordinal))
        {
            return ContractAssembly;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    /// <summary>
    /// 解析並載入 Unmanaged (原生) DLL。
    /// </summary>
    /// <param name="unmanagedDllName">Unmanaged DLL 名稱。</param>
    /// <returns>載入之程式庫指標，若找不到則回傳 IntPtr.Zero。</returns>
    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
