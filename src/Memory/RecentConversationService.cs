using System.Globalization;
using System.Text;

namespace BackgroundAssistant.Memory;

/// <summary>
/// 最近對話上下文協調服務。
/// 配合 GlobalStateService 確保單一正在進行的回合，並使用 BM25 演算法篩選最近完成的對話回合以提供給 Prompt 使用。
/// </summary>
public sealed class RecentConversationService
{
    private readonly AgentMemoryDatabase _database;
    private readonly Bm25RelevanceScorer _relevanceScorer;
    private readonly ILogger<RecentConversationService> _logger;
    private readonly int _maxTurns;
    private readonly double _minimumScore;
    private readonly object _gate = new();
    private string? _pendingUserText;

    /// <summary>
    /// 初始化 <see cref="RecentConversationService"/> 的新執行個體。
    /// </summary>
    /// <param name="database">對話記憶資料庫。</param>
    /// <param name="relevanceScorer">BM25 相關性評分器。</param>
    /// <param name="configuration">應用程式組態設定。</param>
    /// <param name="logger">記錄器實例。</param>
    public RecentConversationService(
        AgentMemoryDatabase database,
        Bm25RelevanceScorer relevanceScorer,
        IConfiguration configuration,
        ILogger<RecentConversationService> logger)
    {
        _database = database;
        _relevanceScorer = relevanceScorer;
        _logger = logger;
        _maxTurns = int.TryParse(configuration["ConversationRelevance:MaxTurns"], out int maxTurns)
            ? Math.Max(0, maxTurns)
            : 2;
        _minimumScore = double.TryParse(
            configuration["ConversationRelevance:MinimumBm25Score"],
            CultureInfo.InvariantCulture,
            out double minimumScore)
            ? Math.Max(0d, minimumScore)
            : 0.25d;
    }

    /// <summary>
    /// 依據當前使用者輸入，從最近歷史對話中以 BM25 篩選高相關性回合並組合成 Prompt 上下文字串。
    /// </summary>
    /// <param name="currentInput">當前使用者輸入文字。</param>
    /// <returns>格式化後的先前對話上下文字串；若無相關對話則回傳空字串。</returns>
    public string BuildPromptContext(string currentInput)
    {
        IReadOnlyList<ConversationTurn> turns = _database.GetRecentTurns(
            AgentMemoryDatabase.LocalUserId,
            _maxTurns);
        if (turns.Count == 0) return "";

        List<ConversationTurn> candidates = turns
            .Where(turn => !IsSameInput(currentInput, turn.UserText))
            .Where(turn => !HasExcessiveRepetition(turn.AssistantText))
            .ToList();
        if (candidates.Count == 0) return "";

        IReadOnlyList<double> scores = _relevanceScorer.Score(
            currentInput,
            candidates.Select(turn => turn.UserText).ToArray());
        var relevantTurns = new List<ConversationTurn>();

        for (int index = 0; index < candidates.Count; index++)
        {
            bool included = scores[index] >= _minimumScore;
            _logger.LogInformation(
                "BM25 recent-turn relevance {score:F3} (minimum {minimum:F3}), included: {included}. Candidate: {candidate}",
                scores[index],
                _minimumScore,
                included,
                candidates[index].UserText);
            Console.WriteLine(
                $"[2. BM25 Context]: score={scores[index]:F3}, included={included}, text={candidates[index].UserText}");

            if (included) relevantTurns.Add(candidates[index]);
        }

        if (relevantTurns.Count == 0) return "";

        var builder = new StringBuilder("相關的先前對話：\n");
        foreach (ConversationTurn turn in relevantTurns)
        {
            builder.Append("使用者：").AppendLine(turn.UserText);
            builder.Append("助理：").AppendLine(turn.AssistantText);
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// 比較當前輸入與歷史輸入是否為相同字句（忽略大小寫與標點符號），以避免 Prompt 重複回灌。
    /// </summary>
    /// <param name="currentInput">當前使用者輸入。</param>
    /// <param name="candidateInput">歷史候選使用者輸入。</param>
    /// <returns>若實質內容相同則為 true，否則為 false。</returns>
    private static bool IsSameInput(string currentInput, string candidateInput)
    {
        static string Normalize(string value) => new(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        return Normalize(currentInput) == Normalize(candidateInput);
    }

    /// <summary>
    /// 檢查文字內容是否包含過度重複的片段（例如小模型常見的跳針現象），若有則排除該歷史回合。
    /// </summary>
    /// <param name="text">要檢查的文字內容。</param>
    /// <returns>若出現重複 6 次以上的字句片段則為 true，否則為 false。</returns>
    private static bool HasExcessiveRepetition(string text)
    {
        string compact = new(text.Where(character => !char.IsWhiteSpace(character)).ToArray());
        for (int phraseLength = 2; phraseLength <= 8; phraseLength++)
        {
            for (int start = 0; start + phraseLength <= compact.Length; start++)
            {
                string phrase = compact.Substring(start, phraseLength);
                int occurrences = 0;
                int position = 0;
                while ((position = compact.IndexOf(phrase, position, StringComparison.Ordinal)) >= 0)
                {
                    occurrences++;
                    position += phraseLength;
                    if (occurrences >= 6) return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// 標記新對話回合開始，並暫存使用者當前輸入文字。
    /// </summary>
    /// <param name="userText">使用者輸入文字。</param>
    public void BeginTurn(string userText)
    {
        lock (_gate)
        {
            if (_pendingUserText is not null)
            {
                _logger.LogWarning("Replacing an incomplete pending conversation turn.");
            }
            _pendingUserText = userText.Trim();
        }
    }

    /// <summary>
    /// 完成當前對話回合，並將使用者輸入與助理回覆非同步寫入 SQLite 資料庫中。
    /// </summary>
    /// <param name="assistantText">助理回覆文字（或工具摘要文字）。</param>
    public void CompleteTurn(string assistantText)
    {
        string? userText;
        lock (_gate)
        {
            userText = _pendingUserText;
            _pendingUserText = null;
        }

        if (userText is null) return;

        try
        {
            _database.AddTurn(AgentMemoryDatabase.LocalUserId, userText, assistantText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to persist a completed conversation turn.");
        }
    }
}
