using Microsoft.VisualBasic;

namespace BackgroundAssistant.Services;

/// <summary>
/// 提供快速的繁簡體中文轉換服務。
/// 使用 .NET 內建的 Microsoft.VisualBasic 庫進行轉換。
/// </summary>
public static class ChineseConverterService
{
    /// <summary>
    /// 將字串轉換為繁體中文 (台灣)。
    /// </summary>
    public static string ToTraditional(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        
        try 
        {
            // LCID 1028 = zh-TW (繁體中文 - 台灣)
            return Strings.StrConv(input, VbStrConv.TraditionalChinese, 1028);
        }
        catch
        {
            // 如果 1028 也不支援，嘗試不帶 LCID (使用系統預設)
            return Strings.StrConv(input, VbStrConv.TraditionalChinese);
        }
    }

    /// <summary>
    /// 將字串轉換為簡體中文 (中國)。
    /// </summary>
    public static string ToSimplified(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return input;
        
        try
        {
            // LCID 2052 = zh-CN (簡體中文 - 中國)
            return Strings.StrConv(input, VbStrConv.SimplifiedChinese, 2052);
        }
        catch
        {
            return Strings.StrConv(input, VbStrConv.SimplifiedChinese);
        }
    }
}
