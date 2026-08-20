using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundAssistant;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ChannelWriter<string> _ttsWriter;

    public Worker(ILogger<Worker> logger, [FromKeyedServices("ExecutionResult")] Channel<string> ttsChannel)
    {
        _logger = logger;
        _ttsWriter = ttsChannel.Writer;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // 等待一小段時間確保所有 HostedServices (如 TTS Worker) 都已經 Ready
            await Task.Delay(2000, stoppingToken);

            _logger.LogInformation("System warm-up complete. Sending notification...");
            
            await _ttsWriter.WriteAsync("暖身完畢，我已經準備好為您服務了。", stoppingToken);

            // 初始化後，這個 Worker 就可以保持安靜，或執行定時任務
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常關閉
        }
    }
}
