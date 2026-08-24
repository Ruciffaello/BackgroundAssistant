using System.Globalization;
using System.Text;

namespace BackgroundAssistant.Memory;

/// <summary>
/// Small-corpus BM25 scorer for deciding whether recent user turns belong in
/// the current conversation prompt. Chinese text is represented by character
/// bigrams; Latin letters and digits are grouped into lowercase words.
/// </summary>
public sealed class Bm25RelevanceScorer
{
    private const double K1 = 1.2;
    private const double B = 0.75;
    private static readonly HashSet<string> StopTerms = new(StringComparer.Ordinal)
    {
        "什麼", "怎麼", "如何", "為什", "請問", "知道", "可以", "一下",
        "幫我", "我想", "這個", "那個", "的是", "有什"
    };

    public IReadOnlyList<double> Score(string query, IReadOnlyList<string> documents)
    {
        if (documents.Count == 0) return [];

        IReadOnlyList<string> queryTerms = Tokenize(query);
        if (queryTerms.Count == 0) return Enumerable.Repeat(0d, documents.Count).ToArray();

        List<IReadOnlyList<string>> documentTerms = documents.Select(Tokenize).ToList();
        double averageLength = Math.Max(1d, documentTerms.Average(terms => terms.Count));
        var documentFrequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            documentFrequency[term] = documentTerms.Count(
                terms => terms.Contains(term, StringComparer.Ordinal));
        }

        var scores = new double[documents.Count];
        for (int index = 0; index < documentTerms.Count; index++)
        {
            IReadOnlyList<string> terms = documentTerms[index];
            Dictionary<string, int> frequencies = terms
                .GroupBy(term => term, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

            foreach (string term in queryTerms.Distinct(StringComparer.Ordinal))
            {
                if (!frequencies.TryGetValue(term, out int frequency)) continue;

                int containingDocuments = documentFrequency[term];
                double idf = Math.Log(
                    1d + (documents.Count - containingDocuments + 0.5d) /
                    (containingDocuments + 0.5d));
                double denominator = frequency + K1 *
                    (1d - B + B * terms.Count / averageLength);
                scores[index] += idf * frequency * (K1 + 1d) / denominator;
            }
        }

        return scores;
    }

    private static IReadOnlyList<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var word = new List<char>();
        var cjkRun = new List<char>();

        void FlushWord()
        {
            if (word.Count == 0) return;
            tokens.Add(new string(word.ToArray()).ToLowerInvariant());
            word.Clear();
        }

        void FlushCjk()
        {
            if (cjkRun.Count == 1)
            {
                tokens.Add(cjkRun[0].ToString());
            }
            else
            {
                for (int i = 0; i < cjkRun.Count - 1; i++)
                {
                    tokens.Add(new string([cjkRun[i], cjkRun[i + 1]]));
                }
            }
            cjkRun.Clear();
        }

        foreach (char character in text.Normalize(NormalizationForm.FormKC))
        {
            UnicodeCategory category = char.GetUnicodeCategory(character);
            bool isCjk = character is >= '\u3400' and <= '\u9fff';
            bool isWordCharacter = char.IsLetterOrDigit(character) && !isCjk;

            if (isCjk)
            {
                FlushWord();
                cjkRun.Add(character);
            }
            else if (isWordCharacter || category == UnicodeCategory.NonSpacingMark)
            {
                FlushCjk();
                word.Add(character);
            }
            else
            {
                FlushWord();
                FlushCjk();
            }
        }

        FlushWord();
        FlushCjk();
        return tokens.Where(term => !StopTerms.Contains(term)).ToArray();
    }
}
