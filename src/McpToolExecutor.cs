using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using BackgroundAssistant.Tools;
using BackgroundAssistant.Memory;
using BackgroundAssistant.PluginRuntime;
using BackgroundAssistant.Services;

namespace BackgroundAssistant;

/// <summary>
/// 第四階段：執行 (Executor/Hands) - 工具執行工作者。
/// 負責解析 JSON 指令並分派給對應的 IMcpTool 實作或 DLL 插件執行，將結果字串寫入 ExecutionResult 通道。
/// </summary>
public class McpToolExecutor : BackgroundService
{
    private readonly ILogger<McpToolExecutor> _logger;
    private readonly ChannelReader<string> _jsonCommandReader;
    private readonly ChannelWriter<string> _resultWriter;
    private readonly IEnumerable<IMcpTool> _tools;
    private readonly RecentConversationService _recentConversation;
    private readonly ToolManifestCatalog _toolManifestCatalog;
    private readonly LazyDllToolLoader _dllToolLoader;
    private readonly GlobalStateService _globalState;

    /// <summary>
    /// 初始化 <see cref="McpToolExecutor"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="jsonCommandChannel">JSON 指令通道。</param>
    /// <param name="executionResultChannel">執行結果文字通道。</param>
    /// <param name="recentConversation">最近對話服務。</param>
    /// <param name="toolManifestCatalog">插件資訊清單目錄。</param>
    /// <param name="dllToolLoader">DLL 工具延遲載入器。</param>
    /// <param name="globalState">全域狀態服務。</param>
    /// <param name="tools">內建靜態 IMcpTool 集合。</param>
    public McpToolExecutor(
        ILogger<McpToolExecutor> logger, 
        [FromKeyedServices("JsonCommand")] Channel<string> jsonCommandChannel,
        [FromKeyedServices("ExecutionResult")] Channel<string> executionResultChannel,
        RecentConversationService recentConversation,
        ToolManifestCatalog toolManifestCatalog,
        LazyDllToolLoader dllToolLoader,
        GlobalStateService globalState,
        IEnumerable<IMcpTool> tools)
    {
        _logger = logger;
        _jsonCommandReader = jsonCommandChannel.Reader;
        _resultWriter = executionResultChannel.Writer;
        _recentConversation = recentConversation;
        _toolManifestCatalog = toolManifestCatalog;
        _dllToolLoader = dllToolLoader;
        _globalState = globalState;
        _tools = tools;
    }

    /// <summary>
    /// 背景執行迴圈：從 JsonCommand 讀取指令，分派至對應工具執行，並將輸出結果送往 TTS 或重設為閒置狀態。
    /// </summary>
    /// <param name="stoppingToken">取消語彙基元。</param>
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
                string memoryText;
                bool speakResult = true;

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
                        memoryText = resultText;
                    }
                    else if (_toolManifestCatalog.TryGetTool(toolName, out var registration))
                    {
                        var execution = await _dllToolLoader.ExecuteAsync(
                            toolName,
                            root,
                            stoppingToken);
                        resultText = execution.Result.Content;
                        memoryText = execution.Result.MemorySummary ?? resultText;
                        speakResult = execution.SpeakResult;

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
                    }
                    else
                    {
                        resultText = "找不到對應的工具來執行此操作。";
                        memoryText = resultText;
                        _logger.LogWarning("Unknown tool requested: {tool}", toolName);
                    }
                }
                catch (PluginLoadException ex)
                {
                    _logger.LogError(ex, "DLL Tool loading failed with {code}.", ex.ErrorCode);
                    resultText = $"工具載入失敗：{ex.Message}";
                    memoryText = "工具載入失敗。";

                    try
                    {
                        using var failedDocument = JsonDocument.Parse(jsonStr);
                        var failedRoot = failedDocument.RootElement;
                        var failedToolName = failedRoot.TryGetProperty("tool", out var failedTool)
                            ? failedTool.GetString() ?? ""
                            : "";
                        if (_toolManifestCatalog.TryGetTool(failedToolName, out var failedRegistration))
                        {
                            speakResult = failedRegistration.Manifest.SpeakResult;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Tool command failed: {msg}", ex.Message);
                    resultText = "指令格式錯誤，無法執行。";
                    memoryText = resultText;
                }

                Console.WriteLine($"[4. Execution Result]: {resultText}");
                if (speakResult)
                {
                    await _resultWriter.WriteAsync(resultText, stoppingToken);
                }
                else
                {
                    _globalState.SetIdle();
                    _logger.LogInformation(
                        "Tool result was displayed without TTS. System is now IDLE.");
                }

                _recentConversation.CompleteTurn(memoryText);
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
