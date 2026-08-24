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

    public string Name => "system_control";

    public SystemTools(
        ILogger<SystemTools> logger,
        IHostApplicationLifetime appLifetime,
        Services.GlobalStateService globalState)
    {
        _logger = logger;
        _appLifetime = appLifetime;
        _globalState = globalState;
    }

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
