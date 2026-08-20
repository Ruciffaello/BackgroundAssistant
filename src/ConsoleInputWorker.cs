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

    public ConsoleInputWorker(
        ILogger<ConsoleInputWorker> logger,
        GlobalStateService globalState,
        [FromKeyedServices("CleanText")] Channel<string> cleanTextChannel)
        : base(logger, globalState, cleanTextChannel)
    {
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("Console Input Worker (CMD) started. You can type commands directly.");

        await Task.Run(async () =>
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    string? line = await Console.In.ReadLineAsync(stoppingToken);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    line = line.Trim();
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
