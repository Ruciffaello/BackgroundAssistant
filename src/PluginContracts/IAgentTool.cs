using System.Text.Json;

namespace BackgroundAssistant.PluginContracts;

/// <summary>
/// BackgroundAssistant 與外部 Tool DLL 共用的最小執行契約。
/// </summary>
public interface IAgentTool
{
    ToolDescriptor Descriptor { get; }

    Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken);
}
