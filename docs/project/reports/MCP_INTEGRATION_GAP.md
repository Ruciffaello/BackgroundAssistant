# MCP 對接差異報告

> 文件目的：釐清 BackgroundAssistant 目前實作與目標 MCP 產品架構之差異，供未來建置雙向 MCP 能力、DLL 插件及合作方工具整合時參考。
>
> 基準日期：2026-08-21；參考 MCP Protocol Revision：2025-11-25。

## 1. 結論

本專案目前的程式實作**尚未成為標準 MCP Client 或 MCP Server**；這是完成度描述，不是產品定位。

現有的 `IMcpTool`、`McpToolExecutor` 是程序內部的工具抽象與分派器：`IntentParserWorker` 產生自訂 JSON，`McpToolExecutor` 依 `tool` 欄位找到 DI 容器中的 `IMcpTool` 並直接呼叫。這個設計在概念上接近 MCP 的 Tools primitive，但尚未實作 MCP 通訊協定。

BackgroundAssistant 的目標不是傳統上只扮演 Client 或只扮演 Server，而是具備下列能力的助理平台：

- **MCP Client 能力**：探索並呼叫合作方或第三方提供的 MCP Server 工具。
- **MCP Server 能力**：將適合對外提供的自身能力暴露給其他 MCP Host／Client。
- **DLL 插件能力**：在同一程序中載入本地插件，完成產品專屬或部署環境限定的功能。
- **統一調度能力**：讓內建工具、DLL 插件與遠端 MCP 工具進入同一個工具目錄及選擇流程。

因此，現階段較準確的描述是：產品目標為**雙向 MCP 能力與本地插件並存的工具協作平台**；目前程式仍是 MCP-ready 的內部工具原型。MCP 協定實作暫不排入近期工作，以下內容作為未來架構依據。

## 2. 現況資料流

```text
Speech / Console
       │
       ▼
IntentParserWorker
       │ 自訂 JSON，例如：
       │ { "tool": "get_time", "location": "Tokyo" }
       ▼
McpToolExecutor
       │ 以 IMcpTool.Name 在程序內查找
       ▼
IMcpTool.ExecuteAsync(JsonElement)
       │
       ▼
純文字結果 → TTS
```

這條流程沒有 MCP Client、MCP Server、transport 或 protocol lifecycle；所有工具與呼叫端都在同一個 .NET Host 內。

## 3. 與標準 MCP 的差異

| 面向 | 專案目前實作 | 標準 MCP | 未來需要補上的能力 |
| --- | --- | --- | --- |
| 系統角色 | 單一程序內的解析器、分派器與工具 | Host 內可管理多個 MCP Client；產品也可另外提供 MCP Server 能力 | 建立雙向角色邊界，避免 Client session、Server endpoint 與助理 Host 生命週期互相耦合 |
| 通訊格式 | Channel 傳遞一般字串及自訂 JSON | UTF-8 JSON-RPC 2.0 request、response、notification | 導入 MCP SDK 或自行實作 JSON-RPC 訊息層 |
| 連線生命週期 | Worker 隨 Host 啟停 | `initialize` → capability/version negotiation → `notifications/initialized` → operation → transport shutdown | 建立 session、版本協商、能力檢查及正常斷線流程 |
| 傳輸 | 僅程序內 Channels | 標準 transport 為 stdio 或 Streamable HTTP | 本機外部工具優先考慮 stdio；遠端服務考慮 Streamable HTTP |
| 工具探索 | 工具在 `Program.cs` 靜態註冊，名稱由程式碼約定 | Client 透過 `tools/list` 動態取得工具 | 合併內建工具、DLL 插件及各外部 server 的工具目錄，並保留來源識別 |
| 工具描述 | `IMcpTool` 只有 `Name` 與執行方法 | Tool 包含名稱、描述、`inputSchema`，可包含 output schema、annotations 等 | 為每個工具建立 JSON Schema 與可供模型判斷的描述 |
| 工具呼叫 | 自訂 `{ "tool": ..., ... }` 後直接呼叫 C# 方法 | Client 送出 `tools/call`，參數置於 `arguments` | 增加本地命令與 MCP `CallToolRequest` 的轉換層 |
| 工具結果 | 單一 `string` | `CallToolResult` 可含多個 content block、structured content、錯誤狀態 | 將文字、結構化資料與錯誤轉成助理可播報的統一結果 |
| 錯誤模型 | 多數錯誤轉成中文純文字 | JSON-RPC protocol error 與 tool execution error 有不同語意 | 保留錯誤類型、重試性及使用者可見訊息 |
| 取消與逾時 | 介面未接收 `CancellationToken`；HTTP 呼叫未統一設定 timeout | 規格建議 request timeout，並支援 cancellation/progress 等能力 | 將取消權杖、逾時與進度一路傳到外部呼叫 |
| 動態變更 | Host 啟動後工具集合固定 | Server 可宣告 `tools.listChanged` 並發送清單變更通知 | 處理 DLL 載入／卸載及遠端工具清單變更，刷新統一目錄 |
| Resources / Prompts | 未實作 | MCP Server 還可提供 Resources、Prompts | 只有出現實際需求時再導入，不是 Tools 對接的前置條件 |
| Sampling / Elicitation / Tasks | 未實作 | 屬額外 client/server capability；Tasks 在本基準版本仍屬實驗性 | 第一階段不應納入最小範圍，避免不必要複雜度 |
| 安全 | 工具皆為本地受信任 DI 元件 | 外部 server 需要信任、權限、參數驗證與傳輸安全邊界 | 建立 allowlist、使用者確認策略、憑證管理與輸出限制 |

## 4. 目標產品架構：雙向 MCP 與 DLL 插件並存

Client 與 Server 不是互斥方案，而是同一產品中的兩個協定邊界；DLL 插件則是第三種、本機程序內的擴充方式。

```text
使用者 / 語音 / 其他輸入
          │
          ▼
BackgroundAssistant Host
          │
          ▼
統一工具目錄與調度層
   ├─ Built-in Adapter ──▶ 內建工具
   ├─ Plugin Adapter  ───▶ DLL 插件
   └─ MCP Client Layer ──▶ 合作方／第三方 MCP Servers
          │
          └─ MCP Server Layer ──▶ 對外提供經授權的產品能力
```

### A. 對外呼叫：MCP Client 能力

用途：讓助理呼叫其他程序或遠端服務提供的 MCP Tools。

建議資料流：

```text
IntentParser / Tool-selection layer
              │
              ▼
Unified Tool Catalog
   ├─ LocalToolAdapter → 現有 IMcpTool
   └─ McpClientAdapter → 外部 MCP Server
                              ├─ stdio
                              └─ Streamable HTTP
```

這個能力負責合作方及第三方工具整合。實作重點：

1. 使用官方 C# SDK 建立及管理 MCP Client。
2. 啟動時連線、協商能力並呼叫 `tools/list`。
3. 將外部工具描述與 input schema 納入意圖／工具選擇流程。
4. 呼叫 `tools/call`，把 MCP content blocks 正規化成現有 TTS 所需文字。
5. 以 server identity、合作方身分或 namespace 避免不同來源的工具名稱衝突。
6. 保留現有本地工具，透過 adapter 與外部工具共存，不必一次重寫。

### B. 對外提供：MCP Server 能力

用途：讓其他 MCP Host／Client 呼叫本專案的時間、新聞或其他本地工具。

建議將可重用工具邏輯從 Hosted Worker 與 TTS 流程分離，再透過官方 SDK 暴露為 MCP Tools。外部呼叫應只取得工具結果，不應默認觸發本機 TTS、關閉整個 Host 或搶佔麥克風工作狀態。

Server 端必須使用獨立的授權與能力清單，不能因為某工具能被本機助理使用，就自動允許外部呼叫。

### C. 本機擴充：DLL 插件能力

DLL 插件用於產品專屬、離線或不適合透過網路協定提供的功能。插件不需要假裝成 MCP Server，但應透過 adapter 轉換成與 MCP Tools 相容的統一工具描述及結果模型。

插件邊界至少需要定義：

- 插件 manifest、識別碼、版本及相容範圍。
- 工具名稱、描述、input schema、output schema 及風險提示。
- 載入、卸載、錯誤隔離與相依套件處理。
- 可信來源、簽章或 allowlist 政策。
- 是否允許插件能力再由 MCP Server 對外暴露。

## 5. 建議的最小遷移順序

未來正式啟動對接時，可按以下順序進行：

1. **定義統一工具模型**：先讓內建工具、DLL 插件及 MCP Tools 共用名稱、描述、schema、來源、風險提示與結果模型。
2. **建立工具來源 adapter**：區分 Built-in、Plugin 與 Remote MCP，避免調度層依賴特定來源。
3. **建立 DLL 插件契約**：定義 manifest、載入生命週期、安全邊界及相容性。
4. **加入 MCP Client 能力**：先連接一個受控合作方 server，完成協商、探索、呼叫、逾時及取消。
5. **整合工具選擇**：讓解析器根據統一工具目錄與 schema 選擇工具，不再只依賴寫死分類。
6. **加入 MCP Server 能力**：建立獨立對外工具清單、授權政策及 endpoint，不直接暴露整個內部目錄。
7. **擴充合作方治理**：管理 server identity、憑證、允許工具、版本相容、稽核與故障降級。
8. **再評估進階能力**：依需求加入 progress、resources、prompts、sampling 或 tasks。

## 6. 對目前程式的具體映射

| 現有元件 | 未來定位建議 |
| --- | --- |
| `IMcpTool` | 改視為 Local Tool contract；由 `LocalToolAdapter` 掛入統一工具目錄 |
| `McpToolExecutor` | 演進為能分派 Built-in、DLL Plugin 與 MCP Remote Tool 的 `ToolDispatcher` |
| `IntentParserWorker` | 從固定分類器演進為根據工具清單與 schema 選擇工具的 planning layer |
| `JsonCommandChannel` | 可繼續作為內部 pipeline，但 payload 應改成 typed command，避免一直傳裸字串 |
| `ExecutionResultChannel` | 接收正規化結果；MCP structured content 應先經 adapter 轉換 |
| `Program.cs` 工具註冊 | 保留內建工具註冊，另加入 plugin catalog、MCP Client connections 與獨立 MCP Server endpoint |
| `GlobalStateService` | 僅管理助理互動與 TTS；不應被當作 MCP session 或 request concurrency 管理器 |

## 7. 安全與營運注意事項

- stdio server 是由 client 啟動的子程序；stdout 必須只輸出合法 MCP 訊息，診斷日誌應寫到 stderr。
- Streamable HTTP server 應驗證 `Origin`；本機服務應綁定 loopback，而非直接監聽所有介面。
- HTTP 授權與 stdio 憑證處理方式不同；stdio 憑證宜由環境提供，不應塞入模型 prompt。
- 外部工具回傳內容是不受信任資料，不能直接當成新的系統指令。
- `system_control` 之類具副作用工具若對外暴露，必須增加明確授權／確認，不能沿用目前無條件執行模式。
- 工具名稱可能衝突，統一目錄應保留 server identity 或 namespace。
- MCP 版本會演進；不要把 protocol version 永久寫死在業務邏輯中，應交由 SDK 協商並記錄相容範圍。

## 8. 未來開始實作前的決策清單

- [x] 產品同時具備 MCP Client、MCP Server 與 DLL 插件能力。
- [ ] 第一個落地範圍是 DLL 插件、合作方 MCP Client 連線，還是對外 MCP Server endpoint？
- [ ] 第一個合作方／第三方 server 使用 stdio 還是 Streamable HTTP？
- [ ] 對外 MCP Server 要提供哪些工具，是否允許轉接 DLL 或遠端 MCP 工具？
- [ ] DLL 插件的契約、manifest、相容版本與信任機制為何？
- [ ] 外部工具由設定檔 allowlist，還是允許動態加入？
- [ ] 哪些工具可以自動執行，哪些必須取得使用者確認？
- [ ] MCP 結構化結果如何轉成繁體中文及 TTS 文字？
- [ ] 外部工具失敗時，要重試、降級至本地工具，還是直接回報？
- [ ] 是否需要同時處理多個工具呼叫；若需要，如何與目前單一 `GlobalStateService` 鎖協調？
- [ ] 是否有 Resources／Prompts 的實際使用案例？若沒有，先只做 Tools。

## 9. 官方參考資料

- [MCP 2025-11-25 Lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle)
- [MCP 2025-11-25 Transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports)
- [MCP Server primitives overview](https://modelcontextprotocol.io/specification/2025-11-25/server/index)
- [MCP Schema reference](https://modelcontextprotocol.io/specification/2025-11-25/schema)
- [Official MCP C# SDK overview](https://csharp.sdk.modelcontextprotocol.io/index.html)
- [Official MCP C# SDK getting started](https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html)
