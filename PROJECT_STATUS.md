# BackgroundAssistant 專案狀態

本文件是專案總覽與文件入口。詳細工作狀態統一記錄於 [TASKS.md](docs/project/TASKS.md)，避免在多份文件重複維護。

## 專案概要

BackgroundAssistant 是以 .NET 10 Worker Service 開發的本地 AI 語音助理。專案使用 `System.Threading.Channels` 串接輸入、文字精煉、意圖解析、本地工具執行與語音輸出，主要推論在本機完成。

目前的 `IMcpTool` 與 `McpToolExecutor` 是程序內的本地工具介面及分派器，尚未實作標準 MCP。產品目標則是同時具備 MCP Client／Server 能力、支援 DLL 功能插件，並能依合作協議呼叫合作方或第三方 MCP Server 工具；協定與插件實作目前暫緩。詳細差異見 [MCP 對接差異報告](docs/project/reports/MCP_INTEGRATION_GAP.md)。

## 目前架構

```text
語音輸入 ──▶ RawText ──▶ TextRefinerWorker ──┐
                                              ├─▶ CleanText
CMD 輸入 ─────────────────────────────────────┘
                                                    │
                                                    ▼
                                           IntentParserWorker
                                           （Decision Router）
                                           ├─ answer / chat / support
                                           ├─ tool / clarify
                                           └─ retrieve（待接入）
                                                    │
                              ┌─────────────────────┴─────────────────────┐
                              ▼                                           ▼
                        LLM 回答／追問                         Tool Planner → McpToolExecutor
                                                                        （本地工具分派器）
                                                    │
                                                    ▼
                                               回應 ──▶ TTS
                                                     │
                                                     `──▶ 背景記憶處理（下一階段）
```

主要組件：

- 輸入：`SpeechToTextWorker`、`ConsoleInputWorker`、`InputWorkerBase`。
- STT：SenseVoiceSmall ONNX 與 NAudio 麥克風輸入。
- 文字精煉及意圖解析：共享的 Phi-3.5 ONNX 模型。
- 決策路由：Phi-3.5 將輸入分流至回答、聊天、情緒支持、工具、追問或檢索。
- 工具：`IMcpTool` 本地工具抽象與靜態 DI 註冊。
- TTS：SherpaOnnx VITS 與 NAudio 播放。
- 狀態控制：`GlobalStateService` 限制同時間只處理一個助理互動。

## 當前狀態

- 目前階段：核心語音助理 Pipeline 已打通，正在整理開發與驗證基礎。
- 正在進行：使用者記憶與個人資料功能已完成精簡版設計，尚未開始實作。
- 下一步：依 [使用者記憶設計](docs/project/reports/USER_MEMORY_DESIGN.md) 建立 SQLite 第一版。
- 主要限制：正式自動化測試及 CI 尚未建立；部分工具仍需完成或校正。
- 最近完成：導入輕量文件管理，並完成現有架構與 MCP 名稱的資訊對齊。

## 開發方向

### Now

- 建立低維護成本且可追蹤的專案開發方式。
- 盤點並穩定現有功能與驗證流程。

### Next

- 實作使用者 Profile、最近對話與背景記憶處理。
- 建立可重複執行的建置與測試基礎。

### Later

- 語音喚醒。
- 建立 DLL 工具插件機制。

### Deferred

- 雙向 MCP 能力與合作方 MCP Server 工具整合（產品定位已確定，實作暫緩）。

## 文件索引

| 文件 | 用途 |
| --- | --- |
| [TASKS.md](docs/project/TASKS.md) | 所有進行中、下一步、待處理、暫緩及最近完成的工作 |
| [DECISIONS.md](docs/project/DECISIONS.md) | 會影響後續方向的重要決策及原因 |
| [USER_MEMORY_DESIGN.md](docs/project/reports/USER_MEMORY_DESIGN.md) | 使用者 Profile、對話上下文與記憶安全規則 |
| [reports/](docs/project/reports/) | 值得長期保留的深入分析與調查報告 |
| [CHANGELOG.md](CHANGELOG.md) | 已完成版本的正式變更歷史 |
| [TEST_GUIDE.md](TEST_GUIDE.md) | 現有人工測試方式與預期行為 |

## 文件維護原則

- 工作狀態只在 `TASKS.md` 維護；本文件只顯示摘要。
- 一般工作只記錄說明與完成條件，有需要才補充下一步、阻礙或驗證。
- Git 已能提供的修改細節不重複寫入文件。
- 完成必須有驗證依據；尚未驗證時不得宣告完成。
- 只有重要且可能再次被詢問的選擇才寫入 `DECISIONS.md`。
- 只有值得未來再次查閱的長篇研究才建立報告。

---

最後更新：2026-08-21
