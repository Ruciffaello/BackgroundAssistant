# BackgroundAssistant

本文件是專案總覽與文件入口。詳細工作狀態統一記錄於 [TASKS.md](docs/project/TASKS.md)。

## 專案概要

BackgroundAssistant 是以 .NET 10 Worker Service 開發的本地 AI 語音助理。系統使用 `System.Threading.Channels` 串接語音／CMD 輸入、文字精煉、LLM 路由、本地工具及 TTS，主要推論在本機完成。

目前架構包含內建 `IMcpTool` 與可延遲載入的 DLL 插件機制（`BackgroundAssistant.PluginRuntime`）。標準 MCP Client／Server 仍屬後續方向。

## 目前架構

```text
語音輸入 -> RawText -> TextRefinerWorker --+
                                           +-> CleanText -> IntentParserWorker
CMD 輸入 ----------------------------------+                  |
                                                              +-- conversation（預設）
                                                              |      +-> BM25 篩選最近兩輪
                                                              |      `-> 對話 LLM
                                                              `-- tool（明確需求）
                                                                     `-> McpToolExecutor
                                                                            |-- 內建 IMcpTool
                                                                            `-- 外部 LazyDllToolLoader
                                                                                   `-> 依 SpeakResult -> TTS / IDLE
```

主要組件：

- 輸入：`SpeechToTextWorker`、`ConsoleInputWorker`、`InputWorkerBase`。
- STT：SenseVoiceSmall ONNX 與 NAudio。
- 文字精煉與路由：共享 Phi-3.5 ONNX 模型。
- 路由：一次輸出 `conversation`／`tool`、`subject`，工具模式同時輸出工具名稱與參數；動態載入外部 Manifest Catalog。
- 對話上下文：SQLite 保存完整回合；最近兩輪以中文字元 bigram BM25 篩選後才加入 Prompt。
- 工具：`IMcpTool`、`ToolManifestCatalog`、`LazyDllToolLoader` 及 `McpToolExecutor`。
- 插件範例：`FileSearchTool`（基於 `ripgrep` 的全磁碟檔名搜尋）。
- TTS：SherpaOnnx VITS 與 NAudio。

## 當前狀態

- 已完成建置與單元測試：單次對話／工具路由、工具直接派送、對話 SQLite、最近兩輪 BM25 篩選、DLL 延遲載入與影子副本、損壞 DLL 回退防護、全磁碟搜尋（12/12 通過）。
- 已完成實機驗證：`file_search` 工具實機全磁碟搜尋驗證成功。
- 尚待實機驗證：BM25 門檻調優、CMD／STT 連續對話回歸。
- 尚未實作：長期記憶抽取、`MemoryItems` 寫入與搜尋、Profile、安全確認、向量搜尋。
- 已知限制：BM25 只比較詞彙，不能取代語意 embedding。

## 開發方向

### Now

- 依 [BM25 測試情境](docs/testing/BM25_TEST_SCENARIOS.md)驗證路由與上下文過濾。
- 收集實際 BM25 分數後校正門檻及中文分詞。

### Next

- 建立 Router、BM25 與 token budget 自動化測試。
- 重新定義長期記憶的最小保存與查詢流程。

### Later

- 語音喚醒。
- 新增更多本機 DLL 插件工具。

### Deferred

- 標準 MCP Client／Server 與合作方工具整合。

## 文件索引

| 文件 | 用途 |
| --- | --- |
| [TASKS.md](docs/project/TASKS.md) | 工作狀態唯一來源 |
| [DECISIONS.md](docs/project/DECISIONS.md) | 重要架構決策 |
| [docs/README.md](docs/README.md) | 完整文件分類與索引 |
| [PARSER_REDESIGN.md](docs/design/PARSER_REDESIGN.md) | 現行 Router 規格 |
| [USER_MEMORY_DESIGN.md](docs/design/USER_MEMORY_DESIGN.md) | 對話資料與未來記憶邊界 |
| [HANDOFF.md](docs/project/HANDOFF.md) | 最近一次開發交接紀錄 |
| [BM25_TEST_SCENARIOS.md](docs/testing/BM25_TEST_SCENARIOS.md) | Router 與 BM25 人工測試劇本 |
| [TEST_GUIDE.md](docs/testing/TEST_GUIDE.md) | 一般人工測試方式 |
| [CHANGELOG.md](CHANGELOG.md) | 正式變更歷史 |

---

最後更新：2026-08-25
