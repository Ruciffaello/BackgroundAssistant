using System.Text.Json;

namespace BackgroundAssistant.Tools;

/// <summary>
/// 定義本地工具 (模擬 MCP 工具) 的通用介面。
/// 所有的擴充功能 (如：天氣、時間、搜尋) 均需實作此介面以掛載至系統。
/// </summary>
public interface IMcpTool
{
    /// <summary>
    /// 工具的唯一名稱。此名稱須與 IntentParser 輸出的 "tool" 欄位對應。
    /// </summary>
    string Name { get; }

    /// <summary>
    /// 執行工具的核心邏輯。
    /// </summary>
    /// <param name="root">IntentParser 傳來的完整 JSON 內容。</param>
    /// <returns>執行結果文字，將會被送往 TTS 播報。</returns>
    Task<string> ExecuteAsync(JsonElement root);
}
