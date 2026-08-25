using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using SherpaOnnx;
using NAudio.Wave;
using BackgroundAssistant.Services;

namespace BackgroundAssistant;

/// <summary>
/// 第五階段：語音 (Voice/TTS) - 文字轉語音工作者。
/// 負責從 ExecutionResult 通道讀取文字，使用 VITS 模型合成語音並透過揚聲器播放。
/// 包含數字轉中文的預處理邏輯，確保合成音質。
/// </summary>
public class TextToSpeechWorker : BackgroundService
{
    private readonly ILogger<TextToSpeechWorker> _logger;
    private readonly GlobalStateService _globalState;
    private readonly ChannelReader<string> _resultReader;

    // 模型路徑
    private const string ModelPath = "D:/models/sherpa-onnx-vits-zh-ll/model.onnx";
    private const string LexiconPath = "D:/models/sherpa-onnx-vits-zh-ll/lexicon.txt";
    private const string TokensPath = "D:/models/sherpa-onnx-vits-zh-ll/tokens.txt";
    private const string DictDirPath = "D:/models/sherpa-onnx-vits-zh-ll/dict";

    /// <summary>
    /// 初始化 <see cref="TextToSpeechWorker"/> 的新執行個體。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="globalState">全域狀態服務。</param>
    /// <param name="executionResultChannel">待朗讀之執行結果文字通道。</param>
    public TextToSpeechWorker(
        ILogger<TextToSpeechWorker> logger, 
        GlobalStateService globalState,
        [FromKeyedServices("ExecutionResult")] Channel<string> executionResultChannel)
    {
        _logger = logger;
        _globalState = globalState;
        _resultReader = executionResultChannel.Reader;
    }

    /// <summary>
    /// 背景執行迴圈：初始化 VITS TTS 引擎，並監聽 ExecutionResult 佇列進行文字轉語音與播放。
    /// </summary>
    /// <param name="stoppingToken">取消語彙基元。</param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TTS Worker (Voice) starting...");

        OfflineTts? tts = null;
        try
        {
            if (File.Exists(ModelPath))
            {
                var config = new OfflineTtsConfig();
                config.Model.Vits.Model = ModelPath;
                config.Model.Vits.Lexicon = LexiconPath;
                config.Model.Vits.Tokens = TokensPath;
                config.Model.Vits.DictDir = DictDirPath;
                config.Model.Vits.NoiseScale = 0.667f;
                config.Model.Vits.NoiseScaleW = 0.8f;
                config.Model.Vits.LengthScale = 1.0f;
                config.Model.NumThreads = 2;
                config.Model.Debug = 0;

                tts = new OfflineTts(config);
                _logger.LogInformation("TTS Engine initialized.");
            }
            else
            {
                _logger.LogWarning("TTS Model not found at {path}.", ModelPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize TTS Engine.");
        }

        try
        {
            await foreach (var text in _resultReader.ReadAllAsync(stoppingToken))
            {
                if (tts == null)
                {
                    _logger.LogWarning("TTS is unavailable. Skipping: {text}", text);
                    _globalState.SetIdle(); // 即使失敗也要釋放鎖
                    continue;
                }

                _logger.LogInformation("Synthesizing speech: {text}", text);

                try
                {
                    Console.WriteLine($"[5. TTS Speaking]: {text}");
                    
                    // 將文字按換行符號切割，逐段合成播放以確保間隔感
                    var segments = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var segment in segments)
                    {
                        if (string.IsNullOrWhiteSpace(segment)) continue;
                        
                        // VITS 模型不認識數字，需轉為中文
                        string processedText = ConvertNumbersToChinese(segment);
                        _logger.LogDebug("Synthesizing segment: {seg}", processedText);
                        
                        var audio = tts.Generate(processedText, 1.0f, 0);

                        if (audio != null && audio.Samples != null && audio.Samples.Length > 0)
                        {
                            PlayAudio(audio.Samples, audio.SampleRate);
                        }
                        
                        // 段落之間稍微停頓 (可選，PlayAudio 本身有播放時間)
                        await Task.Delay(200, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "TTS Synthesis failed.");
                }
                finally
                {
                    // 任務結束（包含語音播報完畢），釋放鎖
                    _globalState.SetIdle();
                    _logger.LogInformation("System is now IDLE and ready for next command.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("TTS Worker stopping...");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in TTS Worker");
        }
        finally
        {
            tts?.Dispose();
        }
    }

    /// <summary>
    /// 將字串中的阿拉伯數字轉換為中文字元 (例如 123 -> 一二三)。
    /// </summary>
    private string ConvertNumbersToChinese(string input)
    {
        string[] chineseNumbers = { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九" };
        return System.Text.RegularExpressions.Regex.Replace(input, @"\d", m => chineseNumbers[int.Parse(m.Value)]);
    }

    /// <summary>
    /// 透過 NAudio 播放 PCM 音訊。
    /// </summary>
    private void PlayAudio(float[] samples, int sampleRate)
    {
        try
        {
            // 將 float[] 轉回 16-bit PCM 位元組
            byte[] byteArray = new byte[samples.Length * 2];
            for (int i = 0; i < samples.Length; i++)
            {
                short val = (short)(Math.Clamp(samples[i], -1f, 1f) * 32767);
                BitConverter.GetBytes(val).CopyTo(byteArray, i * 2);
            }

            using var ms = new MemoryStream(byteArray);
            using var rawReader = new RawSourceWaveStream(ms, new WaveFormat(sampleRate, 16, 1));
            using var waveOut = new WaveOutEvent();
            
            waveOut.Init(rawReader);
            waveOut.Play();
            
            while (waveOut.PlaybackState == PlaybackState.Playing)
            {
                Thread.Sleep(100);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audio playback failed.");
        }
    }
}
