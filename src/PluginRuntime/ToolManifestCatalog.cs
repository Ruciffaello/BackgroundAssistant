using System.Text;
using System.Text.Json;

namespace BackgroundAssistant.PluginRuntime;

/// <summary>
/// 插件清單目錄管理員。
/// 負責在應用程式啟動時掃描指定目錄下的 plugin.json，驗證並建立工具中繼資料清單，但不預先載入任何 DLL。
/// </summary>
public sealed class ToolManifestCatalog
{
    /// <summary>
    /// 目前 Host 支援的契約版本號。
    /// </summary>
    public const int SupportedContractVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IReadOnlyDictionary<string, ToolManifestRegistration> _tools;

    /// <summary>
    /// 初始化 <see cref="ToolManifestCatalog"/> 的新執行個體，並掃描指定目錄下所有 plugin.json。
    /// </summary>
    /// <param name="pluginRootDirectory">插件存放的根目錄路徑。</param>
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

    /// <summary>
    /// 取得插件根目錄的完整路徑。
    /// </summary>
    public string PluginRootDirectory { get; }

    /// <summary>
    /// 取得所有已成功註冊的插件清單。
    /// </summary>
    public IReadOnlyCollection<ToolManifestRegistration> Tools => _tools.Values.ToArray();

    /// <summary>
    /// 取得在掃描清單時遭遇之錯誤或無效設定記錄。
    /// </summary>
    public IReadOnlyList<PluginCatalogIssue> Issues { get; }

    /// <summary>
    /// 嘗試依工具識別名稱取得插件註冊資料。
    /// </summary>
    /// <param name="toolName">工具唯一名稱。</param>
    /// <param name="registration">若找到則回傳註冊資料，否則為 null。</param>
    /// <returns>若找到工具則為 true，否則為 false。</returns>
    public bool TryGetTool(string toolName, out ToolManifestRegistration registration) =>
        _tools.TryGetValue(toolName, out registration!);

    /// <summary>
    /// 建立供 Router System Prompt 使用的外部工具精簡描述清單。
    /// </summary>
    /// <returns>格式化之工具名稱、必要參數與描述文字。</returns>
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

    /// <summary>
    /// 從 JSON Schema 的 required 陣列中擷取必要屬性名稱集合。
    /// </summary>
    /// <param name="inputSchema">工具輸入結構 JSON Element。</param>
    /// <returns>必要屬性名稱序列。</returns>
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

    /// <summary>
    /// 嘗試解析並註冊單一 plugin.json 清單檔案。
    /// </summary>
    /// <param name="manifestPath">plugin.json 檔案路徑。</param>
    /// <param name="tools">已註冊工具字典。</param>
    /// <param name="issues">問題記錄清單。</param>
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

    /// <summary>
    /// 驗證插件清單欄位完整性與格式合法性。
    /// </summary>
    /// <param name="manifest">反序列化後之清單物件。</param>
    /// <exception cref="InvalidDataException">當欄位缺少、契約版本不符或格式錯誤時擲出。</exception>
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

    /// <summary>
    /// 解析並確保目標路徑位於指定的插件目錄內，防止路徑周遊安全漏洞。
    /// </summary>
    /// <param name="directory">插件根目錄。</param>
    /// <param name="relativePath">相對路徑。</param>
    /// <returns>安全解析後的完整路徑。</returns>
    /// <exception cref="InvalidDataException">當路徑為絕對路徑或試圖跳出目錄時擲出。</exception>
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

    /// <summary>
    /// 依作業系統取得適當的路徑比對規則。
    /// </summary>
    /// <returns>Windows/macOS 為忽略大小寫，其餘為區分大小寫。</returns>
    private static StringComparison GetPathComparison() =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
