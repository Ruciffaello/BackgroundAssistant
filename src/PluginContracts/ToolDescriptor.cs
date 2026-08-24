namespace BackgroundAssistant.PluginContracts;

/// <summary>
/// 提供給 Host 與 Router 的工具描述。
/// </summary>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    string InputSchema,
    bool SpeakResult = true);
