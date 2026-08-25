using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 系統控制工具：支援透過語音或指令關閉/退出應用程式。
/// </summary>
public class SystemTools : IMcpTool
{
    private readonly ILogger<SystemTools> _logger;
    private readonly IHostApplicationLifetime _appLifetime;
    private readonly Services.GlobalStateService _globalState;

    /// <summary>
    /// 工具唯一識別名稱。
    /// </summary>
    public string Name => "system_control";

    /// <summary>
    /// 初始化 <see cref="SystemTools"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="appLifetime">應用程式生命週期控制。</param>
    /// <param name="globalState">全域狀態服務。</param>
    public SystemTools(
        ILogger<SystemTools> logger,
        IHostApplicationLifetime appLifetime,
        Services.GlobalStateService globalState)
    {
        _logger = logger;
        _appLifetime = appLifetime;
        _globalState = globalState;
    }

    /// <summary>
    /// 執行系統控制指令（觸發優雅關閉流程，並等待當前語音播報結束後終止程式）。
    /// </summary>
    /// <param name="root">JSON 參數元素。</param>
    /// <returns>告別語音播報文字。</returns>
    public Task<string> ExecuteAsync(JsonElement root)
    {
        _logger.LogInformation("SystemTools: Shutdown requested.");

        // 工具結果返回後由 TTS 播放；背景工作等待整個回合真正進入 Idle 再關閉。
        _ = Task.Run(async () =>
        {
            await _globalState.WaitUntilIdleAsync();
            _logger.LogInformation("SystemTools: Stopping application...");
            _appLifetime.StopApplication();
        });

        return Task.FromResult("好的，助手即將關閉，期待下次為您服務，再見！");
    }
}
