namespace BackgroundAssistant.Prompting;

/// <summary>
/// 依模型 Context Window 建立提示詞，讓目前使用者輸入優先於歷史上下文。
/// </summary>
public static class PromptBudgetBuilder
{
    public static PromptBudgetResult Build(
        string systemPrompt,
        string userTemplate,
        string currentInput,
        string? recentContext,
        int contextLimit,
        int reservedOutputTokens,
        int safetyMargin,
        Func<string, int> countTokens)
    {
        ArgumentNullException.ThrowIfNull(countTokens);

        int maxInputTokens = contextLimit - reservedOutputTokens - safetyMargin;
        if (maxInputTokens <= 0)
        {
            throw new InvalidOperationException("The configured context budget leaves no room for prompt input.");
        }

        string normalizedCurrentInput = currentInput.Trim();
        if (string.IsNullOrWhiteSpace(normalizedCurrentInput))
        {
            throw new ArgumentException("Current input cannot be empty.", nameof(currentInput));
        }

        string templateOnlyPrompt = Render(userTemplate, systemPrompt, "");
        if (countTokens(templateOnlyPrompt) > maxInputTokens)
        {
            throw new PromptTemplateTooLongException(contextLimit, reservedOutputTokens, safetyMargin);
        }

        string currentOnlyPrompt = Render(userTemplate, systemPrompt, normalizedCurrentInput);
        if (countTokens(currentOnlyPrompt) > maxInputTokens)
        {
            throw new PromptInputTooLongException(contextLimit, reservedOutputTokens, safetyMargin);
        }

        if (string.IsNullOrWhiteSpace(recentContext))
        {
            return new PromptBudgetResult(currentOnlyPrompt, false);
        }

        string promptWithContext = Render(
            userTemplate,
            systemPrompt,
            ComposeInput(normalizedCurrentInput, recentContext));
        if (countTokens(promptWithContext) <= maxInputTokens)
        {
            return new PromptBudgetResult(promptWithContext, false);
        }

        return new PromptBudgetResult(currentOnlyPrompt, true);
    }

    public static string ComposeInput(string currentInput, string recentContext) =>
        $"以下是先前對話，只用來理解上下文：\n{recentContext.Trim()}\n\n目前使用者輸入（請以這句為主）：\n{currentInput.Trim()}";

    private static string Render(string userTemplate, string systemPrompt, string inputText) =>
        userTemplate
            .Replace("{SystemPrompt}", systemPrompt)
            .Replace("{InputText}", inputText);
}

public sealed record PromptBudgetResult(string Prompt, bool RecentContextOmitted);

public sealed class PromptInputTooLongException : InvalidOperationException
{
    public PromptInputTooLongException(int contextLimit, int reservedOutputTokens, int safetyMargin)
        : base($"Current input exceeds the {contextLimit}-token context budget after reserving output and safety tokens.")
    {
        ContextLimit = contextLimit;
        ReservedOutputTokens = reservedOutputTokens;
        SafetyMargin = safetyMargin;
    }

    public int ContextLimit { get; }
    public int ReservedOutputTokens { get; }
    public int SafetyMargin { get; }
}

public sealed class PromptTemplateTooLongException : InvalidOperationException
{
    public PromptTemplateTooLongException(int contextLimit, int reservedOutputTokens, int safetyMargin)
        : base($"Prompt template exceeds the {contextLimit}-token context budget after reserving output and safety tokens.")
    {
        ContextLimit = contextLimit;
        ReservedOutputTokens = reservedOutputTokens;
        SafetyMargin = safetyMargin;
    }

    public int ContextLimit { get; }
    public int ReservedOutputTokens { get; }
    public int SafetyMargin { get; }
}
