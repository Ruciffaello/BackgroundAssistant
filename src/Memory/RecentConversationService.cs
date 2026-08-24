using System.Globalization;
using System.Text;

namespace BackgroundAssistant.Memory;

/// <summary>
/// Coordinates the single in-flight turn guaranteed by GlobalStateService and
/// exposes only the two most recent completed turns as prompt context.
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

    private static bool IsSameInput(string currentInput, string candidateInput)
    {
        static string Normalize(string value) => new(
            value.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());

        return Normalize(currentInput) == Normalize(candidateInput);
    }

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
