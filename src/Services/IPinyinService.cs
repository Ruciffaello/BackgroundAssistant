namespace BackgroundAssistant.Services;

public interface IPinyinService
{
    /// <summary>
    /// 將中文轉換為標準化的拼音 (預設為純小寫、無空格)
    /// </summary>
    string GetNormalizedPinyin(string input);
    
    /// <summary>
    /// 將中文轉換為拼音陣列 (保留詞彙邊界)
    /// </summary>
    string[] GetPinyinArray(string input);
}
