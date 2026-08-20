# BackgroundAssistant 專案開發進度報告

這份文件記錄了 **BackgroundAssistant** 本地 AI 語音助手的目前開發狀態、架構設計與模型配置。

## 1. 專案核心架構
本專案基於 **.NET 10 Worker Service** 開發，採用非同步管道 (System.Threading.Channels) 架構，確保各個處理階段解耦且高效。

### Pipeline 完整處理流程
目前專案已實現完整的五階段 Pipeline，並加入快速路徑優化：

1.  **輸入端 (Input Layer - Multi-Source)**:
    *   **基底架構**: `InputWorkerBase` 統一管理狀態搶佔 (`GlobalStateService.TryAcquire`) 與通道分派。
    *   **語音輸入**: `SpeechToTextWorker` (SenseVoiceSmall ONNX) -> 監聽麥克風 -> 寫入 `RawTextChannel`。
    *   **終端機文字輸入**: `ConsoleInputWorker` -> 讀取 CMD 鍵盤輸入 -> 直通 `CleanTextChannel`。
2.  **精煉 (Refiner)**:
    *   組件: `TextRefinerWorker` (Phi-3.5 ONNX)
    *   邏輯: 移除語音輸入中的「那個、呃」等贅字。手打文字可繞過此階段。
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
| **Input** | `InputWorkerBase` | 支援 `ConsoleInputWorker` (CMD) 與 `SpeechToTextWorker` (STT)，由 `appsettings.json` 控制開關。 |
| **STT** | SenseVoiceSmall (int8) | 支援中英日韓，鎖定中文過濾；執行緒鎖定 2 以兼顧 4GB 記憶體限制。 |
| **LLM** | Phi-3.5 (Int4 AWQ) | 共享單例服務，動態計算 Max Length (上限 512) 縮小 KV Cache。 |
| **TTS** | VITS (Chinese LL) | 支援繁體中文，具備阿拉伯數字轉中文預處理，執行緒鎖定 2。 |
| **DB** | SQLite (Microsoft.Data.Sqlite) | 存儲熱詞映射，支援自動拼音索引。 |
| **Tools** | `IMcpTool` 多元實作 | `TimeTools`, `PtcgTools`, `NewsTools`, `RssNewsTools`, `KnowledgeTools`, `HumorTools`, `SystemTools`。 |
| **Config** | JSON (`appsettings.json`, `prompts.json`) | 外部化輸入開關、ONNX 設定與提示詞，啟動時自動同步熱詞。 |

---

## 3. 關鍵技術亮點
*   **全本地化推論**: 保護隱私，無雲端 API 成本。
*   **多來源輸入架構**: 透過 `InputWorkerBase` 解耦輸入管道，手打指令秒級直通，語音指令安全過濾。
*   **4GB 記憶體極致調優**:
    *   宿主層：切換為 .NET Workstation GC，立省 150MB~300MB。
    *   推論層：動態計算 KV Cache 長度，短句推論節省 70%~80% 臨時張量。
*   **拼音雙重防護**: 
    *   前端：SQLite 拼音比對 (秒回)。
    *   後端：PinyinCorrectionService 針對 AI 提取結果校正。
*   **RSS 新聞搜尋**: 透過 `RssNewsTools` 對接 Google News，提升中文資訊獲取能力且免 API Key。
*   **指令衝突與安全控制**: 透過 `GlobalStateService.TryAcquire` 原子鎖，確保「說完一個，才聽下一個」，避免連續返回現象。
*   **優雅結束機制**: 支援 CMD 關鍵字 (`exit`/`quit`) 與語音指令 (`system_control`) 安全釋放所有資源並退出。

---

## 4. 待優化與後續計畫
1.  ✅ **指令衝突優化 (已完成)**: 實作狀態鎖定機制。
2.  ✅ **SQLite 快速搜尋 (已完成)**: 實作拼音感知的熱詞快速路徑。
3.  ✅ **資料同步機制 (已完成)**: 支援 JSON 批次匯入熱詞。
4.  ✅ **多來源輸入擴充 (已完成)**: 實作 `InputWorkerBase` 與 CMD 文字輸入 `ConsoleInputWorker`。
5.  ✅ **優雅結束與 4GB 記憶體調優 (已完成)**: 實作 Workstation GC、動態 Max Length 與 `SystemTools`。
6.  **語音喚醒機制 (Wake-word Detection)**: 引入喚醒詞，降低 CPU 常駐開銷。
7.  **上下文對話記憶 (Contextual Memory)**: 實作對話歷史快取，支援連續追問。
8.  **第四階段架構升級 (進行中)**: 實作動態插件化 (Reflection)，支援 DLL 擴充工具。

---
*最後更新日期：2026年8月20日*
