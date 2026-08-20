using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using BackgroundAssistant.Services;

namespace BackgroundAssistant;

/// <summary>
/// 多類型輸入基底類別：封裝所有輸入來源的共通行為（狀態搶佔、日誌記錄、通道分派）。
/// </summary>
public abstract class InputWorkerBase : BackgroundService
{
    protected readonly ILogger Logger;
    protected readonly GlobalStateService GlobalState;
    protected readonly ChannelWriter<string> OutputWriter;

    /// <summary>
    /// 輸入來源名稱標籤 (例如 "CMD", "STT", "WebAPI")。
    /// </summary>
    public abstract string SourceName { get; }

    protected InputWorkerBase(
        ILogger logger,
        GlobalStateService globalState,
        Channel<string> targetChannel)
    {
        Logger = logger;
        GlobalState = globalState;
        OutputWriter = targetChannel.Writer;
    }

    /// <summary>
    /// 統一由基底類別處理狀態檢查、搶佔鎖定與派發至目標 Channel。
    /// </summary>
    /// <param name="text">輸入文字內容</param>
    /// <param name="ct">取消權杖</param>
    /// <returns>若成功搶佔狀態並派發則回傳 true，若系統忙碌中則回傳 false。</returns>
    protected async Task<bool> DispatchInputAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // 原子性搶佔狀態鎖
        if (!GlobalState.TryAcquire())
        {
            Logger.LogWarning("[{source}] 系統忙碌中，忽略輸入: {text}", SourceName, text);
            return false;
        }

        Console.WriteLine($"\n[1. {SourceName} Input]: {text}");

        // 寫入 Pipeline 下一個階段的 Channel
        await OutputWriter.WriteAsync(text, ct);
        return true;
    }
}
