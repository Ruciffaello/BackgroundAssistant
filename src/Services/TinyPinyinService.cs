using TinyPinyin;

namespace BackgroundAssistant.Services;

/// <summary>
/// 基於 TinyPinyin 實作的中文轉拼音服務。
/// </summary>
public class TinyPinyinService : IPinyinService
{
    /// <summary>
    /// 將中文轉換為標準化的拼音（去除空格並轉為全小寫）。
    /// </summary>
    /// <param name="input">原始中文字串。</param>
    /// <returns>標準化無空格之小寫拼音字串。</returns>
    public string GetNormalizedPinyin(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        
        // TinyPinyin 預設輸出大寫空格分隔，我們轉成純小寫無空格
        return PinyinHelper.GetPinyin(input).Replace(" ", "").ToLower();
    }

    /// <summary>
    /// 將中文轉換為拼音字串陣列（保留詞彙分割邊界）。
    /// </summary>
    /// <param name="input">原始中文字串。</param>
    /// <returns>小寫拼音單字陣列。</returns>
    public string[] GetPinyinArray(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return Array.Empty<string>();

        // 取得空格分隔的大寫拼音，轉為小寫陣列
        return PinyinHelper.GetPinyin(input)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.ToLower())
            .ToArray();
    }
}
