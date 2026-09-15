using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace BackgroundAssistant;

/// <summary>
/// Phi-3.5 推論服務的最小公開邊界。
/// Worker 只提出生成需求，不直接持有 ONNX Model、Tokenizer 或同步鎖。
/// </summary>
public interface IPhi35ModelService
{
    int CountTokens(string text);

    Task<Phi35GenerationResult> GenerateAsync(
        Phi35GenerationRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// 單次 Phi-3.5 生成所需的既有設定。
/// </summary>
public sealed record Phi35GenerationRequest(
    string Prompt,
    int ContextLimit,
    int MaxOutputTokens,
    int MinimumTotalTokens = 0,
    double RepetitionPenalty = 1d,
    bool DetectRepeatedSuffix = false);

/// <summary>
/// Phi-3.5 生成結果。
/// </summary>
public sealed record Phi35GenerationResult(string Text, Phi35GenerationStopReason StopReason);

public enum Phi35GenerationStopReason
{
    Completed,
    EndMarker,
    RepeatedSuffix,
    Failed
}

/// <summary>
/// Phi-3.5 模型服務，負責單例模型、序列化推論、ONNX 原生資源與生成停止條件。
/// </summary>
public sealed class Phi35ModelService : IPhi35ModelService, IDisposable
{
    private const string ModelFolderPath = "D:/models/Phi-3.5-mini-instruct-onnx";
    private readonly ILogger<Phi35ModelService> _logger;
    private readonly SemaphoreSlim _generationLock = new(1, 1);
    private Model _model = null!;
    private Tokenizer _tokenizer = null!;

    public Phi35ModelService(ILogger<Phi35ModelService> logger)
    {
        _logger = logger;
        InitializeModel();
    }

    public int CountTokens(string text)
    {
        using var sequences = _tokenizer.Encode(text);
        return sequences[0].Length;
    }

    public async Task<Phi35GenerationResult> GenerateAsync(
        Phi35GenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Prompt);
        if (request.ContextLimit <= 0 || request.MaxOutputTokens <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Context and output token limits must be positive.");
        }

        bool lockTaken = false;
        try
        {
            await _generationLock.WaitAsync(cancellationToken);
            lockTaken = true;

            using var generatorParams = new GeneratorParams(_model);
            using var sequences = _tokenizer.Encode(request.Prompt);
            int inputTokens = sequences[0].Length;
            int minimumTotalTokens = Math.Min(
                Math.Max(inputTokens, request.MinimumTotalTokens),
                request.ContextLimit);
            int maxLength = Math.Clamp(
                inputTokens + request.MaxOutputTokens,
                minimumTotalTokens,
                request.ContextLimit);

            generatorParams.SetSearchOption("max_length", maxLength);
            generatorParams.SetSearchOption("do_sample", false);
            generatorParams.SetSearchOption("repetition_penalty", request.RepetitionPenalty);
            generatorParams.SetSearchOption("past_present_share_buffer", true);

            using var generator = new Generator(_model, generatorParams);
            generator.AppendTokenSequences(sequences);
            using var tokenizerStream = _tokenizer.CreateStream();

            string result = "";
            while (!generator.IsDone())
            {
                cancellationToken.ThrowIfCancellationRequested();
                generator.GenerateNextToken();
                string part = tokenizerStream.Decode(generator.GetSequence(0)[^1]);
                if (string.IsNullOrEmpty(part)) continue;

                result += part;
                int endIndex = result.IndexOf("[END]", StringComparison.Ordinal);
                if (endIndex >= 0)
                {
                    return new Phi35GenerationResult(result[..endIndex], Phi35GenerationStopReason.EndMarker);
                }

                if (request.DetectRepeatedSuffix && TryTrimRepeatedSuffix(result, out string trimmed))
                {
                    _logger.LogWarning("Answer generation stopped after detecting a repeated suffix.");
                    return new Phi35GenerationResult(trimmed, Phi35GenerationStopReason.RepeatedSuffix);
                }
            }

            return new Phi35GenerationResult(result, Phi35GenerationStopReason.Completed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Phi-3.5 inference failed.");
            return new Phi35GenerationResult("", Phi35GenerationStopReason.Failed);
        }
        finally
        {
            if (lockTaken)
            {
                _generationLock.Release();
            }
        }
    }

    private void InitializeModel()
    {
        _logger.LogInformation("Phi35ModelService: Initializing shared model from {path}...", ModelFolderPath);
        if (!Directory.Exists(ModelFolderPath))
        {
            throw new DirectoryNotFoundException($"Phi-3.5 model folder not found at {ModelFolderPath}");
        }

        try
        {
            _model = new Model(ModelFolderPath);
            _tokenizer = new Tokenizer(_model);
            _logger.LogInformation("Phi35ModelService: Shared model loaded successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Phi35ModelService: Failed to load shared model.");
            throw;
        }
    }

    private static bool TryTrimRepeatedSuffix(string text, out string trimmed)
    {
        const int repetitions = 4;
        trimmed = text;
        if (text.Length < 24) return false;

        for (int phraseLength = 2; phraseLength <= 24; phraseLength++)
        {
            int repeatedLength = phraseLength * repetitions;
            if (repeatedLength > text.Length) break;

            string phrase = text[^phraseLength..];
            bool repeated = true;
            for (int index = 2; index <= repetitions; index++)
            {
                int start = text.Length - phraseLength * index;
                if (!text.AsSpan(start, phraseLength).SequenceEqual(phrase))
                {
                    repeated = false;
                    break;
                }
            }

            if (!repeated) continue;
            trimmed = text[..(text.Length - repeatedLength + phraseLength)].TrimEnd();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        _tokenizer?.Dispose();
        _model?.Dispose();
        _generationLock.Dispose();
    }
}
