using System.Text.Json;
using BackgroundAssistant.PluginContracts;
using BackgroundAssistant.PluginRuntime;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant.Tools;

/// <summary>
/// Host 端統一的工具執行入口，將內建工具與 DLL 插件轉為相同的結果與輸出政策。
/// </summary>
public sealed class ToolExecutionService
{
    private readonly ILogger<ToolExecutionService> _logger;
    private readonly IReadOnlyDictionary<string, IMcpTool> _builtInTools;
    private readonly ToolManifestCatalog _toolManifestCatalog;
    private readonly LazyDllToolLoader _dllToolLoader;

    public ToolExecutionService(
        ILogger<ToolExecutionService> logger,
        IEnumerable<IMcpTool> tools,
        ToolManifestCatalog toolManifestCatalog,
        LazyDllToolLoader dllToolLoader)
    {
        _logger = logger;
        _builtInTools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        _toolManifestCatalog = toolManifestCatalog;
        _dllToolLoader = dllToolLoader;
    }

    public int ToolCount => _builtInTools.Count + _toolManifestCatalog.Tools.Count;

    public async Task<ToolExecution> ExecuteAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return Failed("找不到對應的工具來執行此操作。", "工具名稱缺失。", "missing_tool");
        }

        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return Failed("指令格式錯誤，無法執行。", "工具參數不是 JSON object。", "invalid_arguments");
        }

        if (_builtInTools.TryGetValue(toolName, out IMcpTool? builtInTool))
        {
            try
            {
                string content = await builtInTool.ExecuteAsync(arguments);
                return new ToolExecution(new ToolResult(true, content), SpeakResult: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Built-in tool {tool} failed.", toolName);
                return Failed("指令格式錯誤，無法執行。", "內建工具執行失敗。", "tool_execution_failed");
            }
        }

        if (!_toolManifestCatalog.TryGetTool(toolName, out ToolManifestRegistration registration))
        {
            _logger.LogWarning("Unknown tool requested: {tool}", toolName);
            return Failed("找不到對應的工具來執行此操作。", "找不到對應的工具。", "tool_not_found");
        }

        if (TryGetMissingRequiredProperty(registration.Manifest.InputSchema, arguments, out string? missingProperty))
        {
            return new ToolExecution(
                new ToolResult(
                    false,
                    $"工具 {toolName} 缺少必要參數：{missingProperty}。",
                    $"工具 {toolName} 缺少必要參數。",
                    "missing_required_argument"),
                registration.Manifest.SpeakResult);
        }

        try
        {
            DllToolExecution execution = await _dllToolLoader.ExecuteAsync(
                toolName,
                arguments,
                cancellationToken);
            if (execution.LoadedNewVersion)
            {
                _logger.LogInformation(
                    "DLL Tool {tool} version {version} was loaded on demand.",
                    toolName,
                    registration.Manifest.Version);
            }

            if (!string.IsNullOrWhiteSpace(execution.ReloadWarning))
            {
                _logger.LogWarning(
                    "DLL Tool {tool} reload warning: {warning}",
                    toolName,
                    execution.ReloadWarning);
            }

            return new ToolExecution(
                execution.Result,
                execution.SpeakResult,
                execution.LoadedNewVersion,
                execution.ReloadWarning);
        }
        catch (PluginLoadException ex)
        {
            _logger.LogError(ex, "DLL Tool {tool} loading failed with {code}.", toolName, ex.ErrorCode);
            return new ToolExecution(
                new ToolResult(
                    false,
                    $"工具載入失敗：{ex.Message}",
                    "工具載入失敗。",
                    ex.ErrorCode),
                registration.Manifest.SpeakResult);
        }
    }

    private static ToolExecution Failed(string content, string memorySummary, string errorCode) =>
        new(new ToolResult(false, content, memorySummary, errorCode), SpeakResult: true);

    private static bool TryGetMissingRequiredProperty(
        JsonElement inputSchema,
        JsonElement arguments,
        out string? missingProperty)
    {
        missingProperty = null;
        if (!inputSchema.TryGetProperty("required", out JsonElement required) ||
            required.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement property in required.EnumerateArray())
        {
            string? propertyName = property.GetString();
            if (string.IsNullOrWhiteSpace(propertyName) ||
                !arguments.TryGetProperty(propertyName, out JsonElement value) ||
                value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
                (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
            {
                missingProperty = propertyName ?? "unknown";
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 統一工具執行後，供 Host 決定顯示、TTS 與對話記憶的結果。
/// </summary>
public sealed record ToolExecution(
    ToolResult Result,
    bool SpeakResult,
    bool LoadedNewVersion = false,
    string? ReloadWarning = null);
