namespace BackgroundAssistant.PluginRuntime;

public sealed class PluginLoadException : Exception
{
    public PluginLoadException(string errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}
