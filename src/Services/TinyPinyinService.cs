using TinyPinyin;

namespace BackgroundAssistant.Services;

public class TinyPinyinService : IPinyinService
{
    public string GetNormalizedPinyin(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        
        // TinyPinyin 預設輸出大寫空格分隔，我們轉成純小寫無空格
        return PinyinHelper.GetPinyin(input).Replace(" ", "").ToLower();
    }

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
