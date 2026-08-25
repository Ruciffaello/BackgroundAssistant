using Microsoft.ML.OnnxRuntimeGenAI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace BackgroundAssistant;

/// <summary>
/// 定義 Phi-3.5 模型服務的介面。
/// </summary>
public interface IPhi35ModelService
{
    /// <summary>
    /// 取得載入的 ONNX GenAI 模型實例。
    /// </summary>
    Model Model { get; }

    /// <summary>
    /// 取得與模型匹配的 Tokenizer。
    /// </summary>
    Tokenizer Tokenizer { get; }

    /// <summary>
    /// 提供排隊鎖，確保多個 Worker 同時存取共享模型時能依序推論，避免記憶體或狀態衝突。
    /// </summary>
    SemaphoreSlim Lock { get; }
}

/// <summary>
/// Phi-3.5 模型服務的實作，負責模型的單例載入與生命週期管理。
/// 使用此服務可避免在每個 Worker 中重複載入模型，顯著節省 VRAM/RAM 佔用。
/// </summary>
public class Phi35ModelService : IPhi35ModelService, IDisposable
{
    private readonly ILogger<Phi35ModelService> _logger;
    public Model Model { get; private set; } = null!;
    public Tokenizer Tokenizer { get; private set; } = null!;
    public SemaphoreSlim Lock { get; } = new SemaphoreSlim(1, 1);

    private const string ModelFolderPath = "D:/models/Phi-3.5-mini-instruct-onnx";

    /// <summary>
    /// 初始化 <see cref="Phi35ModelService"/> 的新執行個體，載入共享的 ONNX 模型。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    public Phi35ModelService(ILogger<Phi35ModelService> logger)
    {
        _logger = logger;
        InitializeModel();
    }

    /// <summary>
    /// 初始化模型：從指定路徑載入 ONNX 模型檔案與 Tokenizer。
    /// </summary>
    private void InitializeModel()
    {
        _logger.LogInformation("Phi35ModelService: Initializing shared model from {path}...", ModelFolderPath);
        
        if (!Directory.Exists(ModelFolderPath))
        {
            throw new DirectoryNotFoundException($"Phi-3.5 model folder not found at {ModelFolderPath}");
        }

        try
        {
            Model = new Model(ModelFolderPath);
            Tokenizer = new Tokenizer(Model);
            _logger.LogInformation("Phi35ModelService: Shared model loaded successfully.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Phi35ModelService: Failed to load shared model.");
            throw;
        }
    }

    /// <summary>
    /// 釋放 Tokenizer 與 Model 之 Unmanaged 原生資源。
    /// </summary>
    public void Dispose()
    {
        Tokenizer?.Dispose();
        Model?.Dispose();
    }
}
