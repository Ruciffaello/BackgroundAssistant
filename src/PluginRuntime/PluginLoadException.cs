namespace BackgroundAssistant.PluginRuntime;

/// <summary>
/// 插件載入或執行時期發生的自定義例外。
/// </summary>
public sealed class PluginLoadException : Exception
{
    /// <summary>
    /// 初始化 <see cref="PluginLoadException"/> 類別的新執行個體。
    /// </summary>
    /// <param name="errorCode">錯誤代碼（例如 manifest_not_found、entry_type_missing 等）。</param>
    /// <param name="message">錯誤訊息。</param>
    /// <param name="innerException">導致此例外的內部例外。</param>
    public PluginLoadException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    /// <summary>
    /// 取得自定義錯誤代碼。
    /// </summary>
    public string ErrorCode { get; }
}
