using System.Threading.Channels;
using BackgroundAssistant;
using BackgroundAssistant.Tools;
using BackgroundAssistant.Services;
using BackgroundAssistant.Memory;
using BackgroundAssistant.PluginRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;

// 設定全域 UTF8 編碼，防止亂碼
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

// 載入外部 Prompt 設定
builder.Configuration.AddJsonFile("prompts.json", optional: false, reloadOnChange: true);

// 註冊基礎服務
builder.Services.AddSingleton<IPhi35ModelService, Phi35ModelService>();
builder.Services.AddSingleton<GlobalStateService>();
builder.Services.AddSingleton<AgentMemoryDatabase>();
builder.Services.AddSingleton<Bm25RelevanceScorer>();
builder.Services.AddSingleton<RecentConversationService>();
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var logger = sp.GetRequiredService<ILogger<ToolManifestCatalog>>();
    var configuredPath = configuration["PluginRuntime:Directory"] ?? "plugins";
    var pluginDirectory = Path.IsPathFullyQualified(configuredPath)
        ? configuredPath
        : Path.Combine(environment.ContentRootPath, configuredPath);
    var catalog = new ToolManifestCatalog(pluginDirectory);

    foreach (var issue in catalog.Issues)
    {
        logger.LogWarning("Skipping invalid plugin manifest {path}: {message}", issue.Path, issue.Message);
    }

    logger.LogInformation(
        "Plugin manifest catalog loaded {count} external tools from {path}.",
        catalog.Tools.Count,
        catalog.PluginRootDirectory);
    return catalog;
});
builder.Services.AddSingleton(sp =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var environment = sp.GetRequiredService<IHostEnvironment>();
    var catalog = sp.GetRequiredService<ToolManifestCatalog>();
    var configuredPath = configuration["PluginRuntime:CacheDirectory"] ?? ".plugin-cache";
    var cacheDirectory = Path.IsPathFullyQualified(configuredPath)
        ? configuredPath
        : Path.Combine(environment.ContentRootPath, configuredPath);
    return new LazyDllToolLoader(catalog, cacheDirectory);
});
builder.Services.AddSingleton<SqliteDatabaseService>(sp => 
{
    var logger = sp.GetRequiredService<ILogger<SqliteDatabaseService>>();
    var pinyin = sp.GetRequiredService<IPinyinService>();
    return new SqliteDatabaseService(logger, pinyin);
});

// 註冊拼音服務與校正服務
builder.Services.AddSingleton<IPinyinService, TinyPinyinService>();
builder.Services.AddSingleton(sp => 
{
    var pinyinService = sp.GetRequiredService<IPinyinService>();
    // 為了 AOT 相容，避免使用 .Get<string[]>()
    var hotwords = builder.Configuration.GetSection("Hotwords").GetChildren()
                    .Select(c => c.Value)
                    .Where(v => v != null)
                    .Cast<string>()
                    .ToArray();
    return new PinyinCorrectionService(pinyinService, hotwords);
});

// 註冊第四階段工具 (解耦架構)
builder.Services.AddSingleton<IMcpTool, TimeTools>();
builder.Services.AddSingleton<IMcpTool, PtcgTools>();
builder.Services.AddSingleton<IMcpTool, NewsTools>();
builder.Services.AddSingleton<IMcpTool, RssNewsTools>();
builder.Services.AddSingleton<IMcpTool, KnowledgeTools>();
builder.Services.AddSingleton<IMcpTool, HumorTools>();
builder.Services.AddSingleton<IMcpTool, SystemTools>();

// 定義 Pipeline Channels
var rawTextChannel = Channel.CreateUnbounded<string>();
var cleanTextChannel = Channel.CreateUnbounded<string>();
var jsonCommandChannel = Channel.CreateUnbounded<string>();
var executionResultChannel = Channel.CreateUnbounded<string>();

// 註冊 Channels (使用 Keyed Services 區分同類型的 Channel)
builder.Services.AddKeyedSingleton("RawText", rawTextChannel);
builder.Services.AddKeyedSingleton("CleanText", cleanTextChannel);
builder.Services.AddKeyedSingleton("JsonCommand", jsonCommandChannel);
builder.Services.AddKeyedSingleton("ExecutionResult", executionResultChannel);

// 讀取輸入源設定開關 (AOT 友善)
var inputConfig = builder.Configuration.GetSection("InputSources");
bool enableSpeech = bool.TryParse(inputConfig["EnableSpeech"], out var es) ? es : true;
bool enableConsole = bool.TryParse(inputConfig["EnableConsole"], out var ec) ? ec : true;

// 註冊 Workers (依據設定啟用對應輸入源)
if (enableSpeech)
{
    builder.Services.AddHostedService<SpeechToTextWorker>();
}

if (enableConsole)
{
    builder.Services.AddHostedService<ConsoleInputWorker>();
}

builder.Services.AddHostedService<TextRefinerWorker>();
builder.Services.AddHostedService<IntentParserWorker>();
builder.Services.AddHostedService<McpToolExecutor>();
builder.Services.AddHostedService<TextToSpeechWorker>();
builder.Services.AddHostedService<Worker>();

using var ogaHandle = new OgaHandle();
using var host = builder.Build();

// 強制初始化 SQLite 資料庫服務
using (var scope = host.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<SqliteDatabaseService>();
    scope.ServiceProvider.GetRequiredService<AgentMemoryDatabase>();
}

host.Run();
