using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace BackgroundAssistant.Services;

/// <summary>
/// 拼音校正服務，負責處理「同音異字」的語音辨識錯誤。
/// 透過編輯距離 (Levenshtein Distance) 演算法，將辨識出的錯誤拼音修正為預設的熱詞。
/// </summary>
public class PinyinCorrectionService
{
    private readonly IPinyinService _pinyinService;
    private readonly Dictionary<string, string> _hotwordMap = new();
    private const int MaxDistance = 2; // 容許的最大拼音誤差

    /// <summary>
    /// 初始化校正服務，並預先建立熱詞的拼音索引。
    /// </summary>
    /// <param name="pinyinService">拼音轉換服務。</param>
    /// <param name="hotwords">要監控的熱詞清單（如：寶可夢名字）。</param>
    public PinyinCorrectionService(IPinyinService pinyinService, IEnumerable<string> hotwords)
    {
        _pinyinService = pinyinService;
        
        foreach (var word in hotwords)
        {
            string pinyin = _pinyinService.GetNormalizedPinyin(word);
            if (!string.IsNullOrEmpty(pinyin) && !_hotwordMap.ContainsKey(pinyin))
            {
                _hotwordMap.Add(pinyin, word);
            }
        }
    }

    /// <summary>
    /// 掃描 JSON 字串，並自動修正其中所有 String 型別的 Property Value。
    /// 此方法常用於 IntentParser 輸出後的後處理。
    /// </summary>
    /// <param name="jsonStr">原始 JSON 指令字串。</param>
    /// <returns>修正後的 JSON 字串，若無修正則回傳原字串。</returns>
    public string CorrectJsonValues(string jsonStr)
    {
        try
        {
            var node = JsonNode.Parse(jsonStr);
            if (node is JsonObject obj)
            {
                bool modified = false;
                foreach (var property in obj.ToList())
                {
                    if (property.Value is JsonValue val && val.TryGetValue<string>(out var originalText))
                    {
                        var corrected = AttemptCorrection(originalText);
                        if (corrected != null && corrected != originalText)
                        {
                            obj[property.Key] = JsonValue.Create(corrected);
                            modified = true;
                        }
                    }
                }
                return modified ? node.ToJsonString() : jsonStr;
            }
        }
        catch
        {
            // 解析失敗不中斷流程
        }
        return jsonStr;
    }

    /// <summary>
    /// 嘗試對單一字串進行拼音校正。
    /// </summary>
    /// <param name="input">待校正的原始文字 (如：分火龍)。</param>
    /// <returns>校正後的正確文字 (如：噴火龍)，若無匹配熱詞則回傳 null。</returns>
    public string? AttemptCorrection(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        string inputPinyin = _pinyinService.GetNormalizedPinyin(input);

        // 1. 完全符合比對
        if (_hotwordMap.TryGetValue(inputPinyin, out var exactMatch))
        {
            return exactMatch;
        }

        // 2. 模糊比對 (Levenshtein Distance)
        string? bestMatch = null;
        int minDistance = int.MaxValue;

        foreach (var kvp in _hotwordMap)
        {
            // 效能優化：長度差太遠直接跳過
            if (Math.Abs(kvp.Key.Length - inputPinyin.Length) > MaxDistance) continue;

            int distance = ComputeLevenshteinDistance(inputPinyin, kvp.Key);
            
            if (distance <= MaxDistance && distance < minDistance)
            {
                minDistance = distance;
                bestMatch = kvp.Value;
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// 計算編輯距離 (Levenshtein Distance)。
    /// 演算法優化版：僅使用兩個陣列進行空間壓縮，適合頻繁調用。
    /// </summary>
    private static int ComputeLevenshteinDistance(string s, string t)
    {
        int n = s.Length;
        int m = t.Length;
        if (n == 0) return m;
        if (m == 0) return n;

        int[] v0 = new int[m + 1];
        int[] v1 = new int[m + 1];

        for (int i = 0; i <= m; i++) v0[i] = i;

        for (int i = 0; i < n; i++)
        {
            v1[0] = i + 1;
            for (int j = 0; j < m; j++)
            {
                int cost = (s[i] == t[j]) ? 0 : 1;
                v1[j + 1] = Math.Min(v1[j] + 1, Math.Min(v0[j + 1] + 1, v0[j] + cost));
            }
            Array.Copy(v1, v0, v0.Length);
        }
        return v0[m];
    }
}
