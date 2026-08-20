# 變更日誌 (Changelog)

本專案遵循 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/) 的格式規範，記錄所有重要更動。

---

## [v0.4.0] - 2026-08-20

### Added (新增)
* **多類型輸入基底架構 (`InputWorkerBase`)**：
  * 建立抽象基底類別 `InputWorkerBase : BackgroundService`，統一封裝狀態檢查、狀態鎖搶佔 (`GlobalState.TryAcquire`)、Log 輸出與通道派發 (`DispatchInputAsync`)。
* **終端機鍵盤直接輸入 (`ConsoleInputWorker`)**：
  * 實作 `ConsoleInputWorker`，在背景非同步讀取 CMD 鍵盤輸入，直接寫入 `CleanText` 通道，跳過贅字過濾模型達到秒級極速回應。
* **輸入源開關配置**：
  * 在 `appsettings.json` 加入 `InputSources` 設定區塊 (`EnableConsole`, `EnableSpeech`)，可在啟動時自由啟用/停用各輸入管道。
* **系統優雅結束機制 (Graceful Shutdown)**：
  * 在 `ConsoleInputWorker` 中加入鍵盤結束指令（支援 `exit`, `quit`, `q`, `結束`, `再見`, `退出`），直接呼叫 `IHostApplicationLifetime.StopApplication()`。
  * 實作 `SystemTools` (MCP 工具)，支援語音指令「關閉系統 / 結束程式」進行優雅下線並播報道別語音。
  * 在 `hotwords_initial.json` 中配置系統關閉相關熱詞，支援 SQLite 快搜直接觸發。

### Refactored (重構)
* **語音輸入統一化 (`SpeechToTextWorker`)**：
  * 改為繼承 `InputWorkerBase`，輸出導向 `RawText` 通道，與鍵盤輸入遵循相同的基底狀態管理。
* **全域狀態安全鎖定 (`GlobalStateService`)**：
  * 實作原子性搶佔方法 `TryAcquire()`，避免語音與鍵盤輸入在同一瞬間觸發時產生 Race Condition。
* **ONNX 速度與 4GB 記憶體極致優化**：
  * 在 `BackgroundAssistant.csproj` 啟用 **Workstation GC** (`ServerGarbageCollection: false`)，顯著降低 .NET 宿主記憶體佔用（立省 150MB~300MB）。
  * 在 `TextRefinerWorker` 與 `IntentParserWorker` 實作 **動態 Max Length 計算** (`inputTokens + maxNewTokens`)，並設定 512 安全硬上限，兼顧短句極速推論與長字串相容性。
  * 外部化 `OnnxSettings` 至 `appsettings.json`（包含 STT / TTS 執行緒與推論配置）。
* **優雅下線例外處理**：
  * 補全所有 Worker 對 `OperationCanceledException` 的專屬捕捉，避免程式關閉時出現不必要的 `crit:` 致命錯誤日誌。

---

## [v0.3.0] - 2026-05-28

### Added (新增)
* **SQLite 快速搜尋路徑 (Fast Path)**：
  * 實作 SqliteDatabaseService 與 TinyPinyinService，優先比對本地 SQLite 資料庫（支援字面匹配與拼音模糊匹配）。
  * 熱詞或常見指令命中時直接跳過 LLM 推論，大幅降低反應延遲。
* **熱詞自動同步機制**：
  * 支援從外部 hotwords_initial.json 批次匯入熱詞至 SQLite 資料庫，便於部署與維護。
* **第四階段 MCP 工具擴充**：
  * 實作 IMcpTool 介面與動態分派機制。
  * 新增工具實作：RssNewsTools (Google News RSS 免 API Key)、PtcgTools (卡牌搜尋)、TimeTools (本地時間)、KnowledgeTools、HumorTools。
* **拼音後校正服務**：
  * 實作 PinyinCorrectionService，針對 AI 意圖解析提取出的 JSON 參數進行模糊拼音校正（解決同音異字問題）。

### Changed (變更)
* 調整 prompts.json 推論長度，將 max_length 設定優化為 512，提升推論穩定性與容量。
* 優化 TTS 播報體驗：在新聞列表標題間加入 200ms 停頓間隔，並強化阿拉伯數字轉繁體中文讀音機制。

### Fixed (修復)
* **指令衝突控制**：實作 GlobalStateService 全域狀態鎖定，確保「說完一個，才聽下一個」，解決語音指令重疊問題。

---

## [v0.2.0] - 2026-05-15

### Added (新增)
* **LLM 意圖解析 (Brain)**：
  * 實作 IntentParserWorker 與 Phi35ModelService，基於 Phi-3.5 DirectML 執行兩階段意圖解析（分類 ➔ 參數提取）。
  * 實作 TextRefinerWorker，利用 Phi-3.5 模型過濾語音贅字（「那個」、「呃」等）。
* **共用推論資源鎖**：
  * 透過 SemaphoreSlim 管理 Phi35ModelService，防止多個 Worker 同時存取 GPU/模型產生競爭衝突。

---

## [v0.1.0] - 2026-05-05

### Added (新增)
* **專案初始化**：
  * 採用 .NET 10 Worker Service 架構，支援 Native AOT 編譯選項。
* **5 階段 Pipeline 架構**：
  * 基於 System.Threading.Channels 建立解耦非同步處理管道 (RawText ➔ CleanText ➔ JsonCommand ➔ ExecutionResult)。
* **語音辨識 (Ear - STT)**：
  * 整合 sherpa-onnx 與 SenseVoiceSmall (ONNX)，實作麥克風收音、VAD 切分與中文過濾。
* **語音合成 (Voice - TTS)**：
  * 整合 sherpa-onnx 與 VITS (Chinese LL)，實作文字轉語音與音訊播放。
