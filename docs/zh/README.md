# BackgroundAssistant 文件索引

[繁體中文](README.md) | [English](../en/README.md)

專案根目錄只保留總覽與版本歷史；工作管理、設計、研究和測試文件集中在 `docs/zh/`。

## 專案管理

| 文件 | 用途 |
| --- | --- |
| [專案總覽 (繁中)](../../README.zh-TW.md) | 現況、架構與文件入口 |
| [Project Overview (En)](../../README.md) | Current status, architecture, and entry point |
| [工作清單](project/TASKS.md) | 進行中、下一步與待處理工作 |
| [重要決策](project/DECISIONS.md) | 長期架構決策及重新評估條件 |
| [交接紀錄](project/HANDOFF.md) | 最近一次開發工作脈絡與驗證結果 |
| [變更日誌](../../CHANGELOG.md) | 已發布及未發布的重要變更 |

## 現行設計

| 文件 | 用途 |
| --- | --- |
| [Parser 重新設計](design/PARSER_REDESIGN.md) | 現行 Conversation／Tool Router 規格 |
| [對話資料與記憶邊界](design/USER_MEMORY_DESIGN.md) | 已實作對話資料與未來長期記憶邊界 |

## 研究與提案

| 文件 | 用途 |
| --- | --- |
| [MCP 對接差異](research/MCP_INTEGRATION_GAP.md) | 現有本地工具與標準 MCP 的差距 |
| [DLL Plugin 熱抽換](research/FUTURE_DLL_PLUGIN_HOT_SWAP.md) | 未來插件架構提案 |

## 測試與驗收

| 文件 | 用途 |
| --- | --- |
| [一般測試指南](testing/TEST_GUIDE.md) | 啟動、工具、CMD、STT 與 TTS 人工測試 |
| [BM25 測試劇本](testing/BM25_TEST_SCENARIOS.md) | Router、主詞與最近對話相關性案例 |
| [對話與 BM25 驗收](testing/USER_MEMORY_VERIFICATION.md) | 對話紀錄與記憶功能驗收清單 |

## 放置原則

- 根目錄：只保留新進開發者需要立即看到的 `README.md`（英文）與 `README.zh-TW.md`（繁體中文）以及 `CHANGELOG.md`。
- `docs/zh/project/`：工作清單、決策與交接紀錄。
- `docs/zh/design/`：目前採用的架構與設計規格。
- `docs/zh/research/`：尚未採用或需要重新評估的研究與提案。
- `docs/zh/testing/`：可重複執行的測試方式、劇本與驗收清單。
