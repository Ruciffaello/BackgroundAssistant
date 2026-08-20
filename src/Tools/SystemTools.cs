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

    public string Name => "system_control";

    public SystemTools(ILogger<SystemTools> logger, IHostApplicationLifetime appLifetime)
    {
        _logger = logger;
        _appLifetime = appLifetime;
    }

    public Task<string> ExecuteAsync(JsonElement root)
    {
        _logger.LogInformation("SystemTools: Shutdown requested.");

        // 在背景非同步延遲 2.5 秒後觸發停止，確保 TTS 播報完成
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            _logger.LogInformation("SystemTools: Stopping application...");
            _appLifetime.StopApplication();
        });

        return Task.FromResult("好的，助手即將關閉，期待下次為您服務，再見！");
    }
}
