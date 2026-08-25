using System.Text.Json;

namespace BackgroundAssistant.PluginRuntime;

/// <summary>
/// 插件資訊清單資料模型，對應 plugin.json 的結構。
/// </summary>
public sealed class ToolManifest
{
    /// <summary>
    /// 工具唯一識別碼（如 file_search）。
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// 插件版本號（如 1.0.0）。
    /// </summary>
    public required string Version { get; init; }

    /// <summary>
    /// 契約版本號（目前支援版本為 1）。
    /// </summary>
    public required int ContractVersion { get; init; }

    /// <summary>
    /// 入口 DLL 檔案名稱（相對於插件目錄）。
    /// </summary>
    public required string EntryAssembly { get; init; }

    /// <summary>
    /// 實作 IAgentTool 的完整型別名稱。
    /// </summary>
    public required string EntryType { get; init; }

    /// <summary>
    /// 工具功能描述，供 Router 決策使用。
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// 工具輸入參數結構之 JSON Element。
    /// </summary>
    public required JsonElement InputSchema { get; init; }

    /// <summary>
    /// 執行結果是否送進 TTS 語音播放（預設為 true）。
    /// </summary>
    public bool SpeakResult { get; init; } = true;
}

/// <summary>
/// 已成功解析並驗證之插件註冊項目。
/// </summary>
/// <param name="Manifest">解析後的清單資料模型。</param>
/// <param name="PluginDirectory">插件所在的目錄完整路徑。</param>
/// <param name="ManifestPath">plugin.json 檔案的完整路徑。</param>
/// <param name="EntryAssemblyPath">入口 DLL 檔案的完整路徑。</param>
public sealed record ToolManifestRegistration(
    ToolManifest Manifest,
    string PluginDirectory,
    string ManifestPath,
    string EntryAssemblyPath);

/// <summary>
/// 插件清單載入或驗證時發生的問題記錄。
/// </summary>
/// <param name="Path">發生問題的 plugin.json 路徑。</param>
/// <param name="Message">錯誤詳細訊息。</param>
public sealed record PluginCatalogIssue(string Path, string Message);
