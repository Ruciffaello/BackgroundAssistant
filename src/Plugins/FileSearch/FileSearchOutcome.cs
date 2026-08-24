namespace BackgroundAssistant.FileSearch;

public sealed record FileSearchOutcome(
    IReadOnlyList<string> Paths,
    FileSearchMatchMode MatchMode,
    bool TimedOut);

public enum FileSearchMatchMode
{
    None,
    Exact,
    Contains
}
