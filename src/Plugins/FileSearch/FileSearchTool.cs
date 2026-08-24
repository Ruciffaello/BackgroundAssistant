using System.Text;
using System.Text.Json;
using BackgroundAssistant.PluginContracts;

namespace BackgroundAssistant.FileSearch;

public sealed class FileSearchTool : IAgentTool
{
    private const string InputSchema = """
        {
          "type": "object",
          "required": ["fileName"],
          "properties": {
            "fileName": {
              "type": "string",
              "description": "要尋找的檔案名稱，不包含目錄路徑"
            }
          },
          "additionalProperties": false
        }
        """;

    private readonly RipgrepFileSearcher _searcher;

    public FileSearchTool()
        : this(new FileSearchOptions())
    {
    }

    public FileSearchTool(FileSearchOptions options)
    {
        _searcher = new RipgrepFileSearcher(options);
    }

    public ToolDescriptor Descriptor { get; } = new(
        "file_search",
        "依照檔名搜尋電腦中的檔案；先使用完整檔名，找不到才回傳名稱包含結果。",
        InputSchema,
        SpeakResult: false);

    public async Task<ToolResult> ExecuteAsync(
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        if (!arguments.TryGetProperty("fileName", out var fileNameElement) ||
            fileNameElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(fileNameElement.GetString()))
        {
            return new ToolResult(
                false,
                "檔案搜尋需要提供 fileName。",
                "檔案搜尋失敗：缺少檔名。",
                "invalid_file_name");
        }

        var fileName = fileNameElement.GetString()!;

        try
        {
            var outcome = await _searcher.SearchAsync(fileName, cancellationToken);

            if (outcome.TimedOut)
            {
                return new ToolResult(
                    false,
                    "檔案搜尋逾時，請縮小搜尋範圍後再試一次。",
                    "檔案搜尋逾時。",
                    "search_timeout");
            }

            if (outcome.Paths.Count == 0)
            {
                return new ToolResult(
                    true,
                    $"找不到檔名符合「{fileName}」的檔案。",
                    "檔案搜尋完成，沒有找到結果。");
            }

            var matchDescription = outcome.MatchMode == FileSearchMatchMode.Exact
                ? "完整檔名相符"
                : "檔名包含相符";
            var content = new StringBuilder()
                .AppendLine($"找到 {outcome.Paths.Count} 個檔案（{matchDescription}）：");

            for (var index = 0; index < outcome.Paths.Count; index++)
            {
                content.Append(index + 1)
                    .Append(". ")
                    .AppendLine(outcome.Paths[index]);
            }

            return new ToolResult(
                true,
                content.ToString().TrimEnd(),
                $"檔案搜尋完成，找到 {outcome.Paths.Count} 個結果。");
        }
        catch (ArgumentException ex)
        {
            return new ToolResult(
                false,
                ex.Message,
                "檔案搜尋失敗：檔名格式錯誤。",
                "invalid_file_name");
        }
        catch (FileSearchDependencyException ex)
        {
            return new ToolResult(
                false,
                $"{ex.Message} 請先安裝 ripgrep，並確認 rg 位於 PATH。",
                "檔案搜尋失敗：找不到 ripgrep。",
                "ripgrep_unavailable");
        }
        catch (FileSearchProcessException ex)
        {
            return new ToolResult(
                false,
                ex.Message,
                "檔案搜尋執行失敗。",
                "ripgrep_failed");
        }
    }
}
