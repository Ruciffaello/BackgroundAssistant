# BackgroundAssistant 專案狀態

本文件是專案總覽與文件入口。詳細工作狀態統一記錄於 [TASKS.md](docs/project/TASKS.md)。

## 專案概要

BackgroundAssistant 是以 .NET 10 Worker Service 開發的本地 AI 語音助理。系統使用 `System.Threading.Channels` 串接語音／CMD 輸入、文字精煉、LLM 路由、本地工具及 TTS，主要推論在本機完成。

目前的 `IMcpTool` 與 `McpToolExecutor` 是程序內工具介面及分派器，並非標準 MCP。標準 MCP Client／Server 與 DLL 插件仍屬後續方向。

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
                                                                            `-> 回應 -> TTS
```

主要組件：

- 輸入：`SpeechToTextWorker`、`ConsoleInputWorker`、`InputWorkerBase`。
- STT：SenseVoiceSmall ONNX 與 NAudio。
- 文字精煉與路由：共享 Phi-3.5 ONNX 模型。
- 路由：一次輸出 `conversation`／`tool`、`subject`，工具模式同時輸出工具名稱與參數。
- 對話上下文：SQLite 保存完整回合；最近兩輪以中文字元 bigram BM25 篩選後才加入 Prompt。
- 工具：`IMcpTool` 與 `McpToolExecutor`。
- TTS：SherpaOnnx VITS 與 NAudio。

## 當前狀態

- 已完成建置層級驗證：單次對話／工具路由、工具直接派送、對話 SQLite、最近兩輪 BM25 篩選。
- 尚待實機驗證：Router JSON 穩定性、BM25 門檻、CMD／STT 連續對話與工具回歸。
- 尚未實作：長期記憶抽取、`MemoryItems` 寫入與搜尋、Profile、安全確認、向量搜尋。
- 已知限制：正式測試專案與 CI 尚未建立；BM25 只比較詞彙，不能取代語意 embedding。

## 開發方向

### Now

- 依 [BM25 測試情境](BM25_TEST_SCENARIOS.md)驗證路由與上下文過濾。
- 收集實際 BM25 分數後校正門檻及中文分詞。

### Next

- 建立 Router、BM25 與 token budget 自動化測試。
- 重新定義長期記憶的最小保存與查詢流程。

### Later

- 語音喚醒。
- DLL 工具插件。

### Deferred

- 標準 MCP Client／Server 與合作方工具整合。

## 文件索引

| 文件 | 用途 |
| --- | --- |
| [TASKS.md](docs/project/TASKS.md) | 工作狀態唯一來源 |
| [DECISIONS.md](docs/project/DECISIONS.md) | 重要架構決策 |
| [PARSER_REDESIGN.md](docs/project/reports/PARSER_REDESIGN.md) | 現行 Router 規格 |
| [USER_MEMORY_DESIGN.md](docs/project/reports/USER_MEMORY_DESIGN.md) | 對話資料與未來記憶邊界 |
| [BM25_TEST_SCENARIOS.md](BM25_TEST_SCENARIOS.md) | Router 與 BM25 人工測試劇本 |
| [TEST_GUIDE.md](TEST_GUIDE.md) | 一般人工測試方式 |
| [CHANGELOG.md](CHANGELOG.md) | 正式變更歷史 |

---

最後更新：2026-08-24
