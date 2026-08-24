# BackgroundAssistant 文件索引

專案根目錄只保留總覽與版本歷史；設計、工作管理和測試文件集中在 `docs/`。

## 專案管理

| 文件 | 用途 |
| --- | --- |
| [專案總覽](../PROJECT_STATUS.md) | 現況、架構與文件入口 |
| [工作清單](project/TASKS.md) | 進行中、下一步與待處理工作 |
| [重要決策](project/DECISIONS.md) | 長期架構決策及重新評估條件 |
| [變更日誌](../CHANGELOG.md) | 已發布及未發布的重要變更 |

## 設計與調查

| 文件 | 用途 |
| --- | --- |
| [Parser 重新設計](project/reports/PARSER_REDESIGN.md) | 現行 Conversation／Tool Router 規格 |
| [對話資料與記憶邊界](project/reports/USER_MEMORY_DESIGN.md) | 已實作對話資料與未來長期記憶邊界 |
| [對話與 BM25 驗收](project/reports/USER_MEMORY_VERIFICATION.md) | 目前階段及未來記憶驗收項目 |
| [MCP 對接差異](project/reports/MCP_INTEGRATION_GAP.md) | 現有本地工具與標準 MCP 的差距 |
| [DLL Plugin 熱抽換](project/reports/FUTURE_DLL_PLUGIN_HOT_SWAP.md) | 未來插件架構提案 |

## 測試

| 文件 | 用途 |
| --- | --- |
| [一般測試指南](testing/TEST_GUIDE.md) | 啟動、工具、CMD、STT 與 TTS 人工測試 |
| [BM25 測試劇本](testing/BM25_TEST_SCENARIOS.md) | Router、主詞與最近對話相關性案例 |

## 放置原則

- 根目錄：只保留新進開發者需要立即看到的總覽與 Changelog。
- `docs/project/`：工作管理與決策。
- `docs/project/reports/`：設計、調查及未來方案。
- `docs/testing/`：可直接執行的人工測試指南與劇本。
