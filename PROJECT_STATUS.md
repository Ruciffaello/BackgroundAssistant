# BackgroundAssistant 專案開發進度報告

這份文件記錄了 **BackgroundAssistant** 本地 AI 語音助手的目前開發狀態、架構設計與模型配置。

## 1. 專案核心架構
本專案基於 **.NET 10 Worker Service** 開發，採用非同步管道 (System.Threading.Channels) 架構，確保各個處理階段解耦且高效。

### Pipeline 完整處理流程
目前專案已實現完整的五階段 Pipeline，並加入快速路徑優化：

1.  **聽取 (Ear - STT)**:
    *   組件: `SpeechToTextWorker` (SenseVoiceSmall ONNX)
    *   邏輯: 監聽麥克風 -> VAD 切分 -> 文字過濾 -> **[狀態檢查]**。
    *   **狀態鎖定**: 若 `GlobalStateService` 為 Busy，則直接丟棄輸入，防止指令堆疊。
2.  **精煉 (Refiner)**:
    *   組件: `TextRefinerWorker` (Phi-3.5 ONNX)
    *   邏輯: 移除「那個、呃」等贅字。強化提示詞約束，防止模型過度「補完」短句。
3.  **解析 (Brain - Parser)**:
    *   組件: `IntentParserWorker`
    *   **快速路徑 (SQLite Fast Path)**: 優先比對 SQLite 資料庫。支援字面匹配與 **拼音匹配** (解決同音異字)。若命中則跳過 LLM。
    *   **AI 路徑 (LLM Inference)**: 若 SQLite 未命中，則進入 Phi-3.5 兩階段解析 (分類 -> 參數提取)。
    *   **拼音後校正**: 針對提取出的 JSON 內容進行模糊拼音修正。
4.  **執行 (Hands - Executor)**:
    *   組件: `McpToolExecutor`
    *   邏輯: 根據 `tool` 名稱分派至 `IMcpTool` 實作（如 `RssNewsTools`, `PtcgTools`, `TimeTools`）。
5.  **語音 (Voice - TTS)**:
    *   組件: `TextToSpeechWorker` (VITS ONNX)
    *   邏輯: 文字轉語音 -> 播放音訊 -> **[釋放鎖定]**。
    *   **自動釋放**: 播報完畢後，將系統狀態設為 Idle，允許接收下一個指令。

---

## 2. 核心組件與模型配置

| 階段 | 技術 / 模型 | 功能關鍵點 |
| :--- | :--- | :--- |
| **STT** | SenseVoiceSmall | 支援中英日韓，本專案鎖定中文過濾。 |
| **LLM** | Phi-3.5 (Int4 AWQ) | 共享單例服務，使用 `SemaphoreSlim` 控制資源競爭。 |
| **TTS** | VITS (Chinese LL) | 支援繁體中文，具備阿拉伯數字轉中文預處理。 |
| **DB** | SQLite (Microsoft.Data.Sqlite) | 存儲熱詞映射，支援自動拼音索引。 |
| **Config** | JSON (hotwords_initial.json) | 外部化熱詞定義，啟動時自動同步至 DB。 |

---

## 3. 關鍵技術亮點
*   **全本地化推論**: 保護隱私，無雲端 API 成本。
*   **拼音雙重防護**: 
    *   前端：SQLite 拼音比對 (秒回)。
    *   後端：PinyinCorrectionService 針對 AI 提取結果校正。
*   **RSS 新聞搜尋**: 透過 `RssNewsTools` 對接 Google News，提升中文資訊獲取能力且免 API Key。
*   **指令衝突控制**: 透過全域狀態鎖，確保「說完一個，才聽下一個」，避免連續返回現象。
*   **Release 友善設計**: 支援從 `hotwords_initial.json` 自動初始化資料庫，便於分發與部署。

---

## 4. 待優化與後續計畫
1.  ✅ **指令衝突優化 (已完成)**: 實作狀態鎖定機制。
2.  ✅ **SQLite 快速搜尋 (已完成)**: 實作拼音感知的熱詞快速路徑。
3.  ✅ **資料同步機制 (已完成)**: 支援 JSON 批次匯入熱詞。
4.  **語音喚醒機制 (Wake-word Detection)**: 引入喚醒詞，降低 CPU 常駐開銷。
5.  **上下文對話記憶 (Contextual Memory)**: 實作對話歷史快取，支援連續追問。
6.  **第四階段架構升級 (進行中)**: 實作動態插件化 (Reflection)，支援 DLL 擴充工具。

---
*最後更新日期：2026年5月28日*
