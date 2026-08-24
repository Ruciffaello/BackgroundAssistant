using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BackgroundAssistant.Tools;
using BackgroundAssistant.Memory;

namespace BackgroundAssistant;

/// <summary>
/// 第四階段：執行 (Executor/Hands) - 工具執行工作者。
/// 負責解析 JSON 指令並分派給對應的 IMcpTool 實作執行，將結果字串寫入 ExecutionResult 通道。
/// </summary>
public class McpToolExecutor : BackgroundService
{
    private readonly ILogger<McpToolExecutor> _logger;
    private readonly ChannelReader<string> _jsonCommandReader;
    private readonly ChannelWriter<string> _resultWriter;
    private readonly IEnumerable<IMcpTool> _tools;
    private readonly RecentConversationService _recentConversation;

    public McpToolExecutor(
        ILogger<McpToolExecutor> logger, 
        [FromKeyedServices("JsonCommand")] Channel<string> jsonCommandChannel,
        [FromKeyedServices("ExecutionResult")] Channel<string> executionResultChannel,
        RecentConversationService recentConversation,
        IEnumerable<IMcpTool> tools)
    {
        _logger = logger;
        _jsonCommandReader = jsonCommandChannel.Reader;
        _resultWriter = executionResultChannel.Writer;
        _recentConversation = recentConversation;
        _tools = tools;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("MCP Tool Executor (Hands) starting with {count} tools loaded...", _tools.Count());

        try
        {
            await foreach (var jsonStr in _jsonCommandReader.ReadAllAsync(stoppingToken))
            {
                if (jsonStr == "無法執行")
                {
                    const string unavailableResponse = "抱歉，我無法理解您的指令。";
                    await _resultWriter.WriteAsync(unavailableResponse, stoppingToken);
                    _recentConversation.CompleteTurn(unavailableResponse);
                    continue;
                }

                _logger.LogInformation("Executing MCP Command: {json}", jsonStr);
                string resultText;

                try
                {
                    using var doc = JsonDocument.Parse(jsonStr);
                    var root = doc.RootElement;
                    
                    // 取得 JSON 中的工具名稱
                    string toolName = root.TryGetProperty("tool", out var t) ? t.GetString()! : "";
                    
                    // 從註冊的工具清單中尋找匹配者
                    var targetTool = _tools.FirstOrDefault(t => t.Name == toolName);
                    
                    if (targetTool != null)
                    {
                        // 執行具體工具邏輯
                        resultText = await targetTool.ExecuteAsync(root);
                    }
                    else
                    {
                        resultText = "找不到對應的工具來執行此操作。";
                        _logger.LogWarning("Unknown tool requested: {tool}", toolName);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("JSON Parsing failed: {msg}", ex.Message);
                    resultText = "指令格式錯誤，無法執行。";
                }

                Console.WriteLine($"[4. Execution Result]: {resultText}");
                // 送往語音播報階段
                await _resultWriter.WriteAsync(resultText, stoppingToken);
                _recentConversation.CompleteTurn(resultText);
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
}
