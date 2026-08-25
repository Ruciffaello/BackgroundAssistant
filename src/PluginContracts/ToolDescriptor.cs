namespace BackgroundAssistant.PluginContracts;

/// <summary>
/// 提供給 Host 與 Router 的工具描述記錄。
/// 用於宣告工具名稱、功能說明、JSON 輸入綱要以及是否需透過語音朗讀結果。
/// </summary>
/// <param name="Name">工具唯一識別名稱（例如 file_search）。</param>
/// <param name="Description">工具功能說明，供 LLM Router 判斷意圖使用。</param>
/// <param name="InputSchema">工具輸入參數的 JSON Schema 定義字串。</param>
/// <param name="SpeakResult">執行完成後是否將結果送入 TTS 朗讀（預設為 true；若為檔案列表等長文本可設為 false）。</param>
public sealed record ToolDescriptor(
    string Name,
    string Description,
    string InputSchema,
    bool SpeakResult = true);
