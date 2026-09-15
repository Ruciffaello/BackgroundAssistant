using BackgroundAssistant.Prompting;

var tests = new (string Name, Action Run)[]
{
    ("預算足夠時保留相關歷史", KeepsRecentContextWhenItFits),
    ("歷史超限時完整保留目前輸入", OmitsContextBeforeCurrentInput),
    ("目前輸入超限時明確失敗", RejectsOversizedCurrentInput),
    ("樣板超限時明確失敗", RejectsOversizedTemplate)
};

int failed = 0;
foreach ((string name, Action run) in tests)
{
    try
    {
        run();
        Console.WriteLine($"PASS: {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL: {name}");
        Console.Error.WriteLine(ex.Message);
    }
}

Console.WriteLine($"完成：{tests.Length - failed}/{tests.Length} 通過。");
return failed == 0 ? 0 : 1;

static void KeepsRecentContextWhenItFits()
{
    PromptBudgetResult result = Build("目前問題", "歷史回合", 100);

    False(result.RecentContextOmitted);
    Contains("歷史回合", result.Prompt);
    Contains("目前問題", result.Prompt);
}

static void OmitsContextBeforeCurrentInput()
{
    const string currentInput = "目前問題必須完整保留";
    PromptBudgetResult result = Build(currentInput, new string('歷', 80), 40);

    True(result.RecentContextOmitted);
    Contains(currentInput, result.Prompt);
    False(result.Prompt.Contains("歷", StringComparison.Ordinal));
}

static void RejectsOversizedCurrentInput()
{
    Throws<PromptInputTooLongException>(() => Build(new string('問', 80), null, 30));
}

static void RejectsOversizedTemplate()
{
    Throws<PromptTemplateTooLongException>(() => PromptBudgetBuilder.Build(
        new string('系', 80),
        "{SystemPrompt}\n{InputText}",
        "問題",
        null,
        contextLimit: 30,
        reservedOutputTokens: 5,
        safetyMargin: 5,
        CountCharacters));
}

static PromptBudgetResult Build(string currentInput, string? recentContext, int contextLimit) =>
    PromptBudgetBuilder.Build(
        "系統",
        "{SystemPrompt}\n{InputText}",
        currentInput,
        recentContext,
        contextLimit,
        reservedOutputTokens: 5,
        safetyMargin: 5,
        CountCharacters);

static int CountCharacters(string value) => value.Length;

static void Contains(string expected, string actual)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected '{expected}' in '{actual}'.");
    }
}

static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true.");
}

static void False(bool value)
{
    if (value) throw new InvalidOperationException("Expected false.");
}

static void Throws<TException>(Action action) where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
}
