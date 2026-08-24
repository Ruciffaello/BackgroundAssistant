using System.Text;
using System.Text.Json;

namespace BackgroundAssistant.PluginRuntime;

public sealed class ToolManifestCatalog
{
    public const int SupportedContractVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<string, ToolManifestRegistration> _tools;

    public ToolManifestCatalog(string pluginRootDirectory)
    {
        PluginRootDirectory = Path.GetFullPath(pluginRootDirectory);
        var tools = new Dictionary<string, ToolManifestRegistration>(StringComparer.Ordinal);
        var issues = new List<PluginCatalogIssue>();

        if (Directory.Exists(PluginRootDirectory))
        {
            foreach (var manifestPath in Directory.EnumerateFiles(
                         PluginRootDirectory,
                         "plugin.json",
                         SearchOption.AllDirectories))
            {
                TryRegisterManifest(manifestPath, tools, issues);
            }
        }

        _tools = tools;
        Issues = issues;
    }

    public string PluginRootDirectory { get; }

    public IReadOnlyCollection<ToolManifestRegistration> Tools => _tools.Values.ToArray();

    public IReadOnlyList<PluginCatalogIssue> Issues { get; }

    public bool TryGetTool(string toolName, out ToolManifestRegistration registration) =>
        _tools.TryGetValue(toolName, out registration!);

    public string BuildRouterCatalog()
    {
        if (_tools.Count == 0)
        {
            return "";
        }

        var builder = new StringBuilder("外部工具：");
        foreach (var registration in _tools.Values.OrderBy(tool => tool.Manifest.Id, StringComparer.Ordinal))
        {
            builder.Append("\n")
                .Append(registration.Manifest.Id)
                .Append('(')
                .AppendJoin(',', GetRequiredProperties(registration.Manifest.InputSchema))
                .Append(")：")
                .Append(registration.Manifest.Description);
        }

        return builder.ToString();
    }

    private static IEnumerable<string> GetRequiredProperties(JsonElement inputSchema)
    {
        if (!inputSchema.TryGetProperty("required", out var required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return required.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>();
    }

    private void TryRegisterManifest(
        string manifestPath,
        IDictionary<string, ToolManifestRegistration> tools,
        ICollection<PluginCatalogIssue> issues)
    {
        try
        {
            var manifest = JsonSerializer.Deserialize<ToolManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions) ?? throw new InvalidDataException("manifest 是空值。");

            ValidateManifest(manifest);

            var pluginDirectory = Path.GetFullPath(Path.GetDirectoryName(manifestPath)!);
            var entryAssemblyPath = ResolveContainedPath(pluginDirectory, manifest.EntryAssembly);

            if (!File.Exists(entryAssemblyPath))
            {
                throw new FileNotFoundException("找不到入口 DLL。", entryAssemblyPath);
            }

            if (tools.ContainsKey(manifest.Id))
            {
                throw new InvalidDataException($"工具名稱重複：{manifest.Id}");
            }

            tools.Add(
                manifest.Id,
                new ToolManifestRegistration(
                    manifest,
                    pluginDirectory,
                    Path.GetFullPath(manifestPath),
                    entryAssemblyPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            issues.Add(new PluginCatalogIssue(manifestPath, ex.Message));
        }
    }

    private static void ValidateManifest(ToolManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            string.IsNullOrWhiteSpace(manifest.EntryAssembly) ||
            string.IsNullOrWhiteSpace(manifest.EntryType) ||
            string.IsNullOrWhiteSpace(manifest.Description))
        {
            throw new InvalidDataException("manifest 缺少必要欄位。");
        }

        if (manifest.ContractVersion != SupportedContractVersion)
        {
            throw new InvalidDataException(
                $"不支援 contractVersion {manifest.ContractVersion}；Host 只支援 {SupportedContractVersion}。");
        }

        if (manifest.InputSchema.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("inputSchema 必須是 JSON object。");
        }

        if (!string.Equals(Path.GetExtension(manifest.EntryAssembly), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("entryAssembly 必須是 DLL。");
        }
    }

    internal static string ResolveContainedPath(string directory, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidDataException("Plugin 路徑不可為絕對路徑。");
        }

        var fullDirectory = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(fullDirectory, relativePath));

        if (!fullPath.StartsWith(fullDirectory, GetPathComparison()))
        {
            throw new InvalidDataException("Plugin 路徑不可離開工具目錄。");
        }

        return fullPath;
    }

    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
