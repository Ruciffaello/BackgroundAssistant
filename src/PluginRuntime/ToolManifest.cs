using System.Text.Json;

namespace BackgroundAssistant.PluginRuntime;

public sealed class ToolManifest
{
    public required string Id { get; init; }

    public required string Version { get; init; }

    public required int ContractVersion { get; init; }

    public required string EntryAssembly { get; init; }

    public required string EntryType { get; init; }

    public required string Description { get; init; }

    public required JsonElement InputSchema { get; init; }

    public bool SpeakResult { get; init; } = true;
}

public sealed record ToolManifestRegistration(
    ToolManifest Manifest,
    string PluginDirectory,
    string ManifestPath,
    string EntryAssemblyPath);

public sealed record PluginCatalogIssue(string Path, string Message);
