using System.Text.Json;
using System.Threading.Channels;
using BackgroundAssistant.Memory;
using BackgroundAssistant.Services;
using BackgroundAssistant.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant;

/// <summary>
/// 工具執行工作者：解析 JSON 指令、透過統一工具入口執行，並交接顯示、TTS 與回合保存。
/// </summary>
public sealed class McpToolExecutor : BackgroundService
{
    private readonly ILogger<McpToolExecutor> _logger;
    private readonly ChannelReader<string> _jsonCommandReader;
    private readonly ChannelWriter<string> _resultWriter;
    private readonly RecentConversationService _recentConversation;
    private readonly ToolExecutionService _toolExecutionService;
    private readonly GlobalStateService _globalState;

    public McpToolExecutor(
        ILogger<McpToolExecutor> logger,
        [FromKeyedServices("JsonCommand")] Channel<string> jsonCommandChannel,
        [FromKeyedServices("ExecutionResult")] Channel<string> executionResultChannel,
        RecentConversationService recentConversation,
        ToolExecutionService toolExecutionService,
        GlobalStateService globalState)
    {
        _logger = logger;
        _jsonCommandReader = jsonCommandChannel.Reader;
        _resultWriter = executionResultChannel.Writer;
        _recentConversation = recentConversation;
        _toolExecutionService = toolExecutionService;
        _globalState = globalState;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "MCP Tool Executor (Hands) starting with {count} available tools.",
            _toolExecutionService.ToolCount);

        try
        {
            await foreach (string jsonCommand in _jsonCommandReader.ReadAllAsync(stoppingToken))
            {
                ToolExecution execution;
                if (jsonCommand == "無法執行")
                {
                    execution = new ToolExecution(
                        new BackgroundAssistant.PluginContracts.ToolResult(
                            false,
                            "抱歉，我無法理解您的指令。",
                            "無法理解工具指令。",
                            "unavailable_command"),
                        SpeakResult: true);
                }
                else
                {
                    execution = await ExecuteCommandAsync(jsonCommand, stoppingToken);
                }

                string resultText = execution.Result.Content;
                string memoryText = execution.Result.MemorySummary ?? resultText;
                Console.WriteLine($"[4. Execution Result]: {resultText}");

                // 在解除忙碌狀態前完成本回合，避免新輸入搶先覆蓋暫存的使用者文字。
                _recentConversation.CompleteTurn(memoryText);

                if (execution.SpeakResult)
                {
                    await _resultWriter.WriteAsync(resultText, stoppingToken);
                }
                else
                {
                    _globalState.SetIdle();
                    _logger.LogInformation("Tool result was displayed without TTS. System is now IDLE.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("MCP Executor stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in MCP Tool Executor");
        }
    }

    private async Task<ToolExecution> ExecuteCommandAsync(string jsonCommand, CancellationToken cancellationToken)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(jsonCommand);
            JsonElement root = document.RootElement;
            string toolName = root.TryGetProperty("tool", out JsonElement tool)
                ? tool.GetString() ?? ""
                : "";

            _logger.LogInformation("Executing tool command: {json}", jsonCommand);
            return await _toolExecutionService.ExecuteAsync(toolName, root, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool command failed: {message}", ex.Message);
            return new ToolExecution(
                new BackgroundAssistant.PluginContracts.ToolResult(
                    false,
                    "指令格式錯誤，無法執行。",
                    "指令格式錯誤。",
                    "invalid_command"),
                SpeakResult: true);
        }
    }
}
