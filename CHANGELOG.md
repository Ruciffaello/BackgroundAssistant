# 變更日誌 (Changelog)

本專案遵循 [Keep a Changelog](https://keepachangelog.com/zh-TW/1.0.0/) 的格式規範，記錄所有重要更動。

---

## [Unreleased] (規劃中 - 多類型輸入擴充)

### 規劃目標
* 支援終端機 (CMD) 文字直接輸入與多輸入來源抽象架構。

### Planned (預計改動)
* **Added**:
  * 建立 InputWorkerBase 抽象基底類別，封裝輸入來源通用行為（狀態檢查、日誌輸出、通道派發 DispatchInputAsync）。
  * 實作 ConsoleInputWorker，支援在 CMD 終端機直接打字下指令，並直通 CleanText 通道以達秒級回應。
  * 在 `appsettings.json` 加入 `InputSources` 設定區塊 (`EnableConsole`, `EnableSpeech`)，支援啟動時彈性開啟/關閉指定輸入源。
* **Refactored**:
  * 重構 SpeechToTextWorker，繼承 InputWorkerBase 以統一輸入架構。
  * 優化 GlobalStateService，加入執行緒安全的原子鎖定搶佔機制 (TryAcquire)。

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
