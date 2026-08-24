# 變更日誌 (Changelog)

本專案遵循 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/) 的格式規範，記錄所有重要更動。

---

## [Unreleased]

### Added (新增)

* 建立 `agent_memory.db` 四表 migration、固定 `local-default` 使用者及完整對話回合寫入。
* 新增最近兩輪 BM25 相關性篩選；中文字元使用 bigram，門檻可由 `ConversationRelevance` 設定。
* 新增 Router 與 BM25 人工測試劇本。
* 新增 DLL 工具插件契約與執行期架構（`BackgroundAssistant.PluginContracts`、`BackgroundAssistant.PluginRuntime`）。
* 實作 `ToolManifestCatalog`：啟動時只掃描 `plugins/*/plugin.json`，不預先載入 DLL。
* 實作 `LazyDllToolLoader`：按需載入、SHA-256 指紋比對、`.plugin-cache/` 影子副本防 Windows 鎖檔、`AssemblyLoadContext` 隔離、更新失敗自動保留舊版實例。
* 實作 `FileSearchTool`（`BackgroundAssistant.FileSearchTool`）：以 `ripgrep` 進行本機檔案搜尋，支援全名優先、包含 fallback、中文與特殊字元，結果不進 TTS。
* 新增 `FileSearchTool.Tests` 測試套件，包含延遲載入、損壞替換防護與全磁碟搜尋等 12 項測試。

### Changed (變更)

* Router 收斂為 `conversation`／`tool`；一般對話為預設路徑。
* Router JSON 新增 `subject`，工具名稱與參數由同一次 LLM 推論直接輸出。
* 移除第二次 Tool Planner 推論及舊 `answer`、`chat`、`support`、`retrieve`、`clarify` 路由。
* 無效 Router 輸出改為回到一般對話，不再一律要求澄清。
* 最近對話放在目前輸入之前，且只有 BM25 達門檻才加入回答 Prompt。
* BM25 排除通用問句詞，避免「什麼」等詞造成跨主題誤命中。
* 相同輸入及具有明顯重複輸出的舊回合不再回灌到回答 Prompt。
* 對話回答加入 repetition penalty 與重複尾段中止，降低小模型無限重複。
* CMD `exit` 與 `system_control` 改為等待目前回應及 TTS 播放完成後再停止 Host。
* 縮短 Router Prompt 與 User Template，外部工具僅注入名稱與必要參數，避免超出 1024 Token 限制。

### Fixed (修復)

* 修復 `RipgrepFileSearcher` 在 Windows 搜尋全磁碟（如 `C:\`、`D:\`）時，因受保護系統目錄「存取被拒 (os error 5)」導致 `rg` 返回 Exit Code 2 而誤判為搜尋失敗的問題。

### Not Implemented (尚未實作)

* `MemoryItems` 長期記憶保存與搜尋、Profile、MemoryWorker、安全確認及向量搜尋。

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
