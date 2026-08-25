using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SherpaOnnx;
using NAudio.Wave;
using BackgroundAssistant.Services;

namespace BackgroundAssistant;

/// <summary>
/// 第一階段：聽取 (Ear) - 語音轉文字工作者。
/// 負責監聽麥克風，使用 VAD (語音活動偵測) 切分音訊，並透過 SenseVoice 模型將語音轉換為原始文字派發至 RawText 通道。
/// </summary>
public class SpeechToTextWorker : InputWorkerBase
{
    /// <summary>
    /// 輸入來源識別名稱。
    /// </summary>
    public override string SourceName => "STT";
    
    // 模型路徑 (已更新為官方 SherpaOnnx 模型路徑)
    private const string ModelPath = "D:/models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/model.int8.onnx";
    private const string TokensPath = "D:/models/sherpa-onnx-sense-voice-zh-en-ja-ko-yue-2024-07-17/tokens.txt";

    /// <summary>
    /// 初始化 <see cref="SpeechToTextWorker"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="globalState">全域狀態服務。</param>
    /// <param name="rawTextChannel">RawText 通道實例。</param>
    public SpeechToTextWorker(
        ILogger<SpeechToTextWorker> logger, 
        GlobalStateService globalState,
        [FromKeyedServices("RawText")] Channel<string> rawTextChannel)
        : base(logger, globalState, rawTextChannel)
    {
    }

    /// <summary>
    /// 背景執行核心邏輯：初始化收音設備並開始監聽。
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Logger.LogInformation("STT Worker (Ear) starting...");

        // 檢查模型檔案是否存在 (暫時僅紀錄，避免啟動失敗)
        if (!File.Exists(ModelPath))
        {
            Logger.LogWarning("Model file not found at {path}. Please update the path later.", ModelPath);
        }

        try
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8; // 確保中文字能顯示
            
            // 初始化 SherpaOnnx OfflineRecognizer
            var config = new OfflineRecognizerConfig();
            config.ModelConfig.SenseVoice.Model = ModelPath;
            config.ModelConfig.Tokens = TokensPath;
            config.ModelConfig.NumThreads = 2;
            config.ModelConfig.Debug = 0; // 關閉冗長的 Debug 訊息
            config.ModelConfig.ModelType = "sense_voice";
            
            using var recognizer = new OfflineRecognizer(config);
            
            // 音訊緩衝區與 VAD 狀態
            var audioBuffer = new List<float>();
            bool isRecording = false;
            int silenceSamples = 0;
            const float VolumeThreshold = 0.05f; // 調高門檻，減少環境噪音干擾
            const int TailSamples = 32000;      // 延長靜音判定時間 (2.0秒)，讓使用者有空間停頓思考

            using var waveIn = new WaveInEvent();
            waveIn.WaveFormat = new WaveFormat(16000, 1);
            waveIn.BufferMilliseconds = 100; // 降低延遲

            waveIn.DataAvailable += async (s, e) =>
            {
                float maxVolume = 0;
                var currentBatch = new float[e.BytesRecorded / 2];

                // 1. 轉換並計算當前片段最大音量
                for (int i = 0; i < currentBatch.Length; i++)
                {
                    currentBatch[i] = BitConverter.ToInt16(e.Buffer, i * 2) / 32768f;
                    maxVolume = Math.Max(maxVolume, Math.Abs(currentBatch[i]));
                }

                // 2. VAD 邏輯
                if (maxVolume > VolumeThreshold)
                {
                    if (!isRecording)
                    {
                        isRecording = true;
                    }
                    silenceSamples = 0; 
                }

                if (isRecording)
                {
                    audioBuffer.AddRange(currentBatch);

                    if (maxVolume <= VolumeThreshold)
                    {
                        silenceSamples += currentBatch.Length;
                    }

                    // 3. 判定結束 (靜音達標 或 緩衝區過大)
                    if (silenceSamples >= TailSamples || audioBuffer.Count > 160000) 
                    {
                        var samples = audioBuffer.ToArray();
                        audioBuffer.Clear();
                        isRecording = false;
                        silenceSamples = 0; 

                        // 只有當音訊長度大於 0.5 秒才處理 (8000 samples)
                        if (samples.Length > 8000)
                        {
                            await ProcessAudioAsync(recognizer, samples, stoppingToken);
                        }
                    }
                }
            };

            waveIn.StartRecording();
            Logger.LogInformation("Microphone listening with VAD (Threshold: {threshold})...", VolumeThreshold);

            try
            {
                // 保持運行直到取消
                await Task.Delay(-1, stoppingToken);
            }
            finally
            {
                waveIn.StopRecording();
                Logger.LogInformation("Microphone stopped.");
            }
        }
        catch (OperationCanceledException)
        {
            Logger.LogInformation("STT Worker stopping...");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error in STT Worker");
        }
    }

    /// <summary>
    /// 將音訊片段送入模型辨識，並進行語言過濾與文字清理。
    /// </summary>
    private async Task ProcessAudioAsync(OfflineRecognizer recognizer, float[] samples, CancellationToken ct)
    {
        try
        {
            using var stream = recognizer.CreateStream();
            stream.AcceptWaveform(16000, samples);
            recognizer.Decode(stream);

            var rawText = stream.Result.Text;
            if (string.IsNullOrWhiteSpace(rawText)) return;

            // Debug 用：印出原始結果與標籤
            Logger.LogInformation("[STT Raw]: {raw}", rawText);

            // 1. 過濾邏輯優化
            // 如果有標籤，則必須包含 <|zh|>
            // 如果沒標籤，則必須包含中文字元
            bool isChinese = rawText.Contains("<|zh|>");
            if (!isChinese)
            {
                // 檢查是否包含任何中文字元 (Unicode 範圍: \u4e00-\u9fa5)
                isChinese = System.Text.RegularExpressions.Regex.IsMatch(rawText, @"[\u4e00-\u9fa5]");
            }

            if (!isChinese)
            {
                Logger.LogDebug("[STT Skip] Non-Chinese or Noise: {raw}", rawText);
                return;
            }

            // 2. 移除所有標籤 (例如 <|zh|>, <|Speech|>, <|with_punc|> 等)
            var cleanText = System.Text.RegularExpressions.Regex.Replace(rawText, @"<\|.*?\|>", "").Trim();
            
            // 3. 額外清理：移除日文字元 (如：あ, ア, い, イ...)
            cleanText = System.Text.RegularExpressions.Regex.Replace(cleanText, @"[\u3040-\u309F\u30A0-\u30FF]", "");

            if (!string.IsNullOrWhiteSpace(cleanText) && cleanText.Length >= 2)
            {
                // 透過基底類別統一搶佔狀態與派發至 RawText 通道
                await DispatchInputAsync(cleanText, ct);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during audio decoding");
        }
    }
}
