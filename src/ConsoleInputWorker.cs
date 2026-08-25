using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BackgroundAssistant.Services;

namespace BackgroundAssistant;

/// <summary>
/// 終端機 (CMD) 文字輸入工作者：從 Console 接收手打指令，直接分派至 CleanText 通道以達秒級回應。
/// </summary>
public class ConsoleInputWorker : InputWorkerBase
{
    public override string SourceName => "CMD";

    private readonly IHostApplicationLifetime _appLifetime;

    /// <summary>
    /// 初始化 <see cref="ConsoleInputWorker"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="globalState">全域狀態服務。</param>
    /// <param name="appLifetime">應用程式生命週期管理。</param>
    /// <param name="cleanTextChannel">CleanText 文字通道。</param>
    public ConsoleInputWorker(
        ILogger<ConsoleInputWorker> logger,
        GlobalStateService globalState,
        IHostApplicationLifetime appLifetime,
        [FromKeyedServices("CleanText")] Channel<string> cleanTextChannel)
        : base(logger, globalState, cleanTextChannel)
    {
        _appLifetime = appLifetime;
    }

    /// <summary>
    /// 背景執行迴圈：監聽終端機鍵盤輸入並分派指令或處理結束動作。
    /// </summary>
    /// <param name="stoppingToken">取消語彙基元。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Console Input Worker (CMD) started. You can type commands directly. (Type 'exit' to quit)");

        await Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    string? line = await Console.In.ReadLineAsync(stoppingToken);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    line = line.Trim();

                    // 結束指令判定
                    if (line.Equals("exit", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("/exit", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("/quit", StringComparison.OrdinalIgnoreCase) ||
                        line.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                        line == "結束" || line == "再見" || line == "退出")
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("\n[系統通知] 收到結束指令，將在目前回應與語音播報完成後關閉 BackgroundAssistant...");
                        Console.ResetColor();

                        await GlobalState.WaitUntilIdleAsync(stoppingToken);
                        _appLifetime.StopApplication();
                        break;
                    }

                    bool dispatched = await DispatchInputAsync(line, stoppingToken);
                    if (!dispatched)
                    {
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.WriteLine("[提示] 系統正在處理或播報其他任務，請稍候再試。");
                        Console.ResetColor();
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error reading from console input.");
                }
            }
        }, stoppingToken);
    }
}
