using System.Reflection;
using System.Runtime.Loader;
using BackgroundAssistant.PluginContracts;

namespace BackgroundAssistant.PluginRuntime;

internal sealed class PluginAssemblyLoadContext : AssemblyLoadContext
{
    private static readonly Assembly ContractAssembly = typeof(IAgentTool).Assembly;
    private static readonly string ContractAssemblyName = ContractAssembly.GetName().Name!;
    private readonly AssemblyDependencyResolver _resolver;

    public PluginAssemblyLoadContext(string mainAssemblyPath)
        : base($"Plugin:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(assemblyName.Name, ContractAssemblyName, StringComparison.Ordinal))
        {
            return ContractAssembly;
        }

        var assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
        return assemblyPath is null ? null : LoadFromAssemblyPath(assemblyPath);
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        var libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return libraryPath is null ? nint.Zero : LoadUnmanagedDllFromPath(libraryPath);
    }
}
