using System.Threading.Channels;
using BackgroundAssistant;
using BackgroundAssistant.Tools;
using BackgroundAssistant.Services;

// 設定全域 UTF8 編碼，防止亂碼
Console.OutputEncoding = System.Text.Encoding.UTF8;

var builder = Host.CreateApplicationBuilder(args);

// 載入外部 Prompt 設定
builder.Configuration.AddJsonFile("prompts.json", optional: false, reloadOnChange: true);

// 註冊基礎服務
builder.Services.AddSingleton<IPhi35ModelService, Phi35ModelService>();
builder.Services.AddSingleton<GlobalStateService>();
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

// 註冊 Workers
builder.Services.AddHostedService<SpeechToTextWorker>();
builder.Services.AddHostedService<TextRefinerWorker>();
builder.Services.AddHostedService<IntentParserWorker>();
builder.Services.AddHostedService<McpToolExecutor>();
builder.Services.AddHostedService<TextToSpeechWorker>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();

// 強制初始化 SQLite 資料庫服務
using (var scope = host.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<SqliteDatabaseService>();
}

host.Run();
