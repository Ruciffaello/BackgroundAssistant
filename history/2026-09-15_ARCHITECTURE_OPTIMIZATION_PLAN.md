# BackgroundAssistant 架構優化施工計畫

- 建立日期：2026-09-15
- 文件狀態：P1～P5 已完成；本機建置與功能驗收已確認正常。
- 目的：依序改善推論服務、對話協調、工具執行與回合完成四個邊界，降低耦合並提升可驗證性。
- 技術方向：先優化現有架構；本輪不導入 Semantic Kernel 或 Microsoft Agent Framework。

## 一、施工原則與範圍

保留 .NET Worker Service、Channels、Phi-3.5 ONNX、STT／TTS、SQLite、BM25 與 DLL 插件架構。以小範圍、可獨立驗收的階段逐步施工，不一次重寫整條管線。

本計畫涵蓋：

1. 集中模型推論與資源管理。
2. 拆分 Router、對話協調與 Worker 職責。
3. 統一內建與 DLL 工具的 Host 呼叫入口。
4. 明確定義回合保存、輸出與恢復 IDLE 的完成順序。
5. 配合上述邊界修正上下文預算裁切策略。

本輪不包含：更換模型、調整為多代理或自主多步工具流程、實作 MCP Client／Server、長期記憶、embedding、向量資料庫、多使用者、插話或並行回合。模型路徑設定化等其他清理，只有在當階段確實必要時才納入。

### 防止過度設計的施工準則

- 每項新增型別都必須直接消除已存在的重複資源管理、分支或已驗證的錯誤；只有「未來可能需要」不是新增理由。
- 不為單一呼叫端建立介面、Factory、Registry、事件匯流排或新的背景 Worker。測試需要時，優先使用純函式、具體型別或既有依賴。
- P2 只集中現有 Phi-3.5 ONNX 的推論程式碼；不建立多模型供應商抽象、串流協定、重試框架或通用 Agent 層。
- P3 只在 Worker 已無法清楚維持單一職責時抽出 Router 或單輪協調；不強制同時建立 `IntentRouter`、`ConversationProcessor`、Session 與 TurnId。
- P4 只建立足以統一現有內建與 DLL 工具呼叫的最小轉接；不重寫 DLL 載入器、不建立通用插件平台，也不更動公開 DLL 契約，除非相容性測試證明必要。
- 每階段先以可觀察行為驗收。若現有責任已清楚、重複已消除、測試可涵蓋，就停止該階段，不為讓架構圖更完整而繼續拆分。
- 新相依套件、資料表、Channel 或長駐服務必須有當前需求與驗收案例；否則延後到真正出現需求時再設計。

既有產品行為應保留：

- 一次處理一個回合，語音與主控台輸入共用後續流程。
- 一般對話為預設路徑，明確工具需求才執行工具。
- 無效路由 JSON、未知模式與不可用工具依既有規則退回對話。
- 近期上下文沿用 SQLite 與 BM25，預設最近兩輪；本輪不順帶改寫相關性演算法。
- DLL 按需載入、版本切換、影子副本與損壞新版回退。
- 檔案搜尋結果只顯示、不進 TTS；記憶保存摘要，不保存完整結果路徑。
- 關閉助手仍需配合告別語音與回合完成順序。

## 二、目前觀察與待驗證風險

以下根據程式碼閱讀，不能視為已完成執行驗證：

| 位置 | 目前觀察 | 施工目的 |
| --- | --- | --- |
| `src/Phi35ModelService.cs` | 介面暴露 ONNX Model、Tokenizer 與 Lock | 上層改為提出推論需求，不自行操作生成器 |
| `src/TextRefinerWorker.cs`、`src/IntentParserWorker.cs` | 各自實作生成流程，長度、停止條件與取消處理不同 | 集中共用執行機制，保留各用途的生成設定 |
| `src/IntentParserWorker.cs` | 同時處理上下文、Prompt、推論、路由、分派與保存 | 拆出可獨立驗證的 Router 與回合協調 |
| `BuildPromptWithinBudget()` | 保留輸入前段，但目前問題放在歷史之後 | 驗證並修正超限時裁掉目前問題的風險 |
| `src/Tools/IMcpTool.cs`、`src/PluginContracts/IAgentTool.cs` | 工具契約、取消支援與結果能力不一致 | 建立統一 Host 執行入口 |
| `src/Memory/RecentConversationService.cs` | 用單一 `_pendingUserText` 暫存回合 | 確認回合歸屬與完成責任 |
| `src/McpToolExecutor.cs` | 非 TTS 分支先 SetIdle，再 CompleteTurn | 驗證新輸入搶先建立回合的競態窗口 |

## 三、目標責任邊界

```text
語音／主控台輸入
        ↓
Channels 與 Workers（傳遞、取消與背景生命週期）
        ↓
ConversationProcessor（單輪協調）
        ├─ 上下文選擇與 Prompt 預算
        ├─ IntentRouter（聊天／工具決策）
        ├─ 推論服務 → Phi-3.5 ONNX
        └─ 統一工具入口
             ├─ 內建工具轉接
             └─ DLL 轉接 → LazyDllToolLoader
        ↓
回合保存與結果輸出
        ├─ Console
        └─ TTS 完成通知
        ↓
恢復接受下一輪輸入
```

圖為責任配置，不要求每個方塊建立獨立專案、介面或 Channel。實作時保留必要的現有管線，只在替換、測試或生命週期邊界建立抽象。

### A. 推論服務

- 接收 Prompt、輸出限制、生成參數與 CancellationToken。
- 集中模型單例、排隊鎖、生成器、原生資源釋放、停止標記與取消檢查。
- 回傳文字與明確完成狀態；token 資訊僅在實際可取得時提供，不臆測數值。
- 保留文字精煉、Router、回答各自的參數與停止政策，不強制統一成同一組設定。
- 不負責決定歷史相關性、工具選擇或保存對話。
- Tokenizer 可透過窄介面提供計數能力，避免上層再次直接依賴 ONNX 型別。
- 不以空字串混淆推論失敗、取消與正常空輸出；由上層決定對使用者的降級回應。

### B. 對話協調與 Router

- Worker 保留通道讀寫與背景生命週期管理。
- IntentRouter 負責路由 Prompt、決策解析與工具有效性檢查，回傳結構化決策。
- ConversationProcessor 安排上下文、路由、回答或工具執行，以及結果交接。
- JSON 解析與降級政策可在不載入模型的情況下測試。
- 第一版保留目前單次 JSON Router，不新增第二次 Planner 或自動工具循環。

### C. 統一工具入口

- Host 以一致的名稱、描述、參數資訊與執行方法使用工具。
- 透過轉接器整合內建 IMcpTool 與外部 IAgentTool；優先保留 DLL 公開契約相容性。
- 結果明確承載成功／失敗、顯示內容、語音政策、記憶摘要與必要錯誤資訊。
- 描述與必要參數盡量來自單一工具目錄；在執行前驗證必要參數。
- 明確處理工具名稱衝突，避免更動後無意改變目前內建工具優先的行為。
- 轉接器只保存工具識別與 loader 依賴，不長期持有 DLL 工具實例。
- 取消能力必須反映真實底層支援；不把僅停止等待誤稱為已終止工具執行。

### D. 回合完成

- 集中決定一輪何時保存、何時輸出、何時可以恢復 IDLE。
- 正常回合只保存一次，結果不能寫到下一輪輸入。
- 非語音結果完成保存嘗試與顯示後，才開放下一輪。
- 語音結果須完成保存嘗試及輸出交接，並在播放結束或明確失敗處理後恢復 IDLE。
- 明確區分正常完成、工具失敗、推論失敗、使用者取消與 Host 停止。
- 保存失敗需記錄且不能造成永久忙碌；是否重試須明確定義，避免重複写入。
- 第一版維持單回合，不預先引入完整 Session 或 TurnId 系統；若測試證明需要識別資訊，再記錄原因與最小設計。

## 四、分階段施工與驗收

施工順序為 P0 → P1 → P2 → P3 → P4 → P5。每階段開始前檢查 Git 差異及實際程式，完成必要驗證後才進下一階段。

### P0：建立行為基準

**範圍**：現有測試、Router／BM25 手動情境、模型與音訊執行環境。

工作：

- 檢查 solution 與現有 FileSearch 測試入口，記錄實際建置和測試結果。
- 使用既有測試文件建立小型固定案例集：一般聊天、時間查詢、檔案搜尋、模糊需求、錯誤 JSON、未知工具、上下文延續、長輸入。
- 實際模型案例與不依賴模型的邏輯測試分開記錄。
- 在環境可用時记录模型、設定、推論次數與端到端延遲，供後續同條件比較。
- 檢查 InputWorkerBase、GlobalStateService、Console／STT／TTS 及 SystemTools 的狀態轉移，確認完成責任。

驗收：

- 有可重複的基準案例與執行指令。
- 清楚記錄通過、失敗及因缺少模型／裝置而未執行的項目。
- 既有問題和本次修改引入的問題可以區分。

### P1：修正上下文裁切與回合順序風險

**範圍**：IntentParserWorker、RecentConversationService、McpToolExecutor，以及必要的狀態／輸出交接程式。

工作：

- 以長歷史加目前問題重現預算裁切風險。
- 將目前輸入與歷史分開組裝：保留必要指示、目前問題及輸出預算，再加入容得下的歷史。
- 超限先減少歷史；目前問題本身過長時，定義可觀察的處理政策，不靜默裁掉核心問題。
- 使用可控制的同步點驗證非 TTS 結果與新輸入交錯，避免依賴隨機等待重現競態。
- 最小修正保存、輸出與 IDLE 順序，檢查 TTS 及助手關閉流程。

驗收：

- 歷史過長時，目前問題完整保留；組裝後 token 數符合預算。
- 目前問題單獨超限及模板單獨超限都有明確測試與處理。
- 非 TTS 完成過程不會把舊結果保存到新輸入。
- 保存失敗、播放失敗不造成永久忙碌或重複保存。
- 關閉助手仍在預期完成點停止。

### P2：集中模型推論

**範圍**：Phi35ModelService、TextRefinerWorker、IntentParserWorker；必要時新增小型推論請求與結果型別。

工作：

- 先建立最小推論邊界，再逐一遷移呼叫端。
- 保留單一模型實例與序列化推論。
- 集中生成器、鎖、停止標記、取消及資源處理。
- 將 P1 的 Prompt 預算透過計數邊界接入，避免預算政策混入通用推論服務。
- 保留各用途既有參數與回答重複偵測行為，任何行為變更另行記錄。

驗收：

- Worker 不再直接建立 ONNX Generator 或自行管理模型鎖。
- 等待鎖期間取消、生成期間取消、推論例外後，後續請求均可繼續工作。
- 模型不會重複載入，正常停止時資源正確釋放。
- 固定案例維持既有功能；效能比較使用相同模型、設定及暖機條件。

### P3：拆出 Router 與對話協調

**範圍**：IntentParserWorker、上下文與回合服務；新增 IntentRouter、ConversationProcessor 或等效元件。

工作：

- Router 回傳結構化決策，移出 JSON 擷取、模式解析與工具有效性檢查。
- 使用明確回傳型別或既有相容訊息，避免把中間狀態只藏在共享欄位。
- ConversationProcessor 組織一輪流程；Worker 保留通道與停止管理。
- 明確規定 Router 與回答各自使用哪些上下文；重構階段先維持既有行為。
- 集中回合完成責任，移除分散且重複的保存觸發點。

驗收：

- 無模型、SQLite、麥克風或 TTS 也能測試路由解析及協調分支。
- 合法工具、聊天、未知工具、缺少欄位、錯誤型別與不合法 JSON 均有明確行為。
- 一般對話、工具成功／失敗、取消各自的完成路徑清楚且不重複保存。
- STT 與主控台仍进入同一對話處理流程。

### P4：統一工具目錄與執行結果

**範圍**：IMcpTool、McpToolExecutor、ToolManifestCatalog、工具轉接器；必要時調整內建工具。

工作：

- 建立統一工具描述及執行入口，先接時間工具與 file_search，再遷移其他內建工具。
- 統一成功、失敗、顯示、語音及記憶摘要的傳遞方式。
- 讓 Router 使用統一目錄產生必要工具資訊，維持精簡 token 使用。
- 保留 LazyDllToolLoader 所有載入與版本控制責任。
- 定義名稱衝突、未知工具、參數缺漏與工具例外處理。

驗收：

- 對話協調層不需要依內建／DLL 來源分派。
- Host 啟動與工具描述列舉不載入 DLL。
- 首次呼叫、版本切換、損壞新版回退與取消回歸通過。
- file_search 仍不進 TTS，記憶摘要不包含完整結果清單。
- 舊 DLL 契約維持相容；如確需破壞性調整，先補遷移設計，不在此階段隱含變更。

### P5：整合驗收與收尾

工作：

- 在相同環境重跑 P0 案例與相關自動測試。
- 執行 CMD／STT → 聊天／工具 → Console／TTS → SQLite → IDLE 的整合驗收。
- 檢查連續輸入、失敗恢復、取消與助手關閉。
- 確認語音輸入在不同階段的忙碌行為與重構前一致。
- 移除遷移後未使用的舊實作，更新架構入口與必要文件。

驗收：

- 四個責任邊界均已落實，沒有新舊流程並行而產生雙重執行或保存。
- 建置與相關測試通過，未驗證的硬體／模型項目明確列出。
- 同條件下沒有未解釋的推論次數或延遲增加；若存在差異，先釐清再結案。
- 後續工作可從文件辨認新的類別責任與測試入口。

## 五、驗證方式與施工控制

既有驗證指令（P0 時確認在當前環境可用）：

```powershell
dotnet build BackgroundAssistant.sln --no-restore -m:1
dotnet run --project tests/FileSearchTool.Tests/BackgroundAssistant.FileSearchTool.Tests.csproj
```

既有參考：

- [工作清單](../docs/zh/project/TASKS.md)
- [架構決策](../docs/zh/project/DECISIONS.md)
- [Router 設計](../docs/zh/design/PARSER_REDESIGN.md)
- [使用者記憶設計](../docs/zh/design/USER_MEMORY_DESIGN.md)
- [BM25 測試情境](../docs/zh/testing/BM25_TEST_SCENARIOS.md)
- [記憶驗收清單](../docs/zh/testing/USER_MEMORY_VERIFICATION.md)
- [一般測試指南](../docs/zh/testing/TEST_GUIDE.md)

控制原則：

- 測試聚焦可觀察行為：回合歸屬、取消、預算、工具結果政策與 DLL 生命週期，不只驗證類別或方法名稱。
- 不以文件既有的「12/12 通過」代替本次執行結果。
- 無法執行實機項目時保留待驗證狀態，不把純邏輯測試當成整合通過。
- 每階段保持差異範圍可審閱；失敗時先停止擴大修改，不用 git reset --hard 覆蓋使用者工作。
- 不為重構刪除或重建使用者資料庫；本輪預期不需要資料表 migration。
- 新增相依套件前，確認現有測試基礎能否滿足需求；不為架構整齊增加不必要框架。
- 實作若需偏離本計畫，先在此文件記錄原因、影響與驗收調整，再依會話授權範圍施工。

## 六、後續工作階段的使用方式

1. 閱讀本文件與 TASKS.md，檢查 Git 狀態及先前施工紀錄。
2. 從第一個未完成階段開始，不預設前次驗證仍適用於已變更程式。
3. 開工時在 TASKS.md 建立或更新對應工作，維持它作為專案工作狀態的唯一來源。
4. 本文件保存施工設計、驗收標準與每階段執行證據；不要再複製一份完整工作清單到其他文件。
5. 階段完成後記錄修改範圍、驗證指令與結果、剩餘限制，以及下一階段入口。
6. 四項優化及整合驗收完成後，才視為本計畫結案。

### 施工紀錄格式

後續每完成一階段，附加以下紀錄：

```text
日期／階段：
修改摘要：
涉及檔案：
驗證指令與結果：
實機驗證與環境：
未完成項目或限制：
計畫偏離與原因：
下一步：
```

目前僅完成本施工文件建立，P0 至 P5 均尚未執行。

## 施工紀錄

### 2026-09-15／P1：上下文預算與回合順序

- 修改摘要：新增 `PromptBudgetBuilder`，先確認目前輸入加樣板可放入預算，再嘗試加入完整相關歷史；若歷史使 Prompt 超限，保留目前輸入並略過整段歷史。使用者輸入或樣板本身超限時改為明確訊息，不再靜默裁切。非 TTS 工具改為在 `SetIdle()` 前完成回合保存。
- 涉及檔案：`src/Prompting/PromptBudgetBuilder.cs`、`src/IntentParserWorker.cs`、`src/McpToolExecutor.cs`、`tests/PromptBudget.Tests/`、solution 與工作清單。
- 驗證指令與結果：`dotnet build BackgroundAssistant.sln --no-restore -m:1` 成功（0 warning、0 error）；`dotnet run --project tests/PromptBudget.Tests/BackgroundAssistant.PromptBudget.Tests.csproj` 為 4/4 通過；`dotnet run --no-build --project tests/FileSearchTool.Tests/BackgroundAssistant.FileSearchTool.Tests.csproj` 為 12/12 通過。
- 實機驗證與環境：尚未執行；需要本機 Phi-3.5、TTS 與輸入設備可用時，依 P1 驗收項目驗證。
- 未完成項目或限制：目前採「保留整段歷史或全部略過」，不裁切半個回合；P3 會有更合適的結構化上下文邊界後，再評估依回合逐筆縮減。
- 計畫偏離與原因：P0 的模型與音訊實機基準因本次作業環境未確認模型與裝置可用，未執行；建置、既有測試與純邏輯回歸已先完成。
- 下一步：在可用硬體環境補做 P1 實機驗收；程式施工進入 P2。

### 2026-09-15／P2：集中模型推論

- 修改摘要：`IPhi35ModelService` 不再公開 ONNX `Model`、`Tokenizer` 或鎖；改提供 token 計數與單次生成。`Phi35ModelService` 集中模型單例、序列化鎖、Generator、停止標記、重複後綴偵測、取消與原生資源釋放。文字精煉與回答維持原有輸出預算與生成參數。
- 涉及檔案：`src/Phi35ModelService.cs`、`src/TextRefinerWorker.cs`、`src/IntentParserWorker.cs`。
- 驗證指令與結果：依使用者目前記憶體限制，未執行建置或測試；已以靜態搜尋確認 Worker 不再直接使用 ONNX `Model`、`Tokenizer`、`Generator` 或同步鎖。
- 實機驗證與環境：尚未執行；需確認等待鎖取消、生成中取消、推論失敗後續請求及正常停止釋放資源。
- 未完成項目或限制：不新增多模型介面、串流、重試或供應商切換；仍只支援目前 Phi-3.5 ONNX 模型。
- 計畫偏離與原因：未執行原訂自動驗收，以遵守目前不可啟動程式的限制。
- 下一步：P3 抽出 Router，保留 Worker 的單輪協調。

### 2026-09-15／P3：拆出單次 Router

- 修改摘要：新增 `IntentRouter`，負責 Router Prompt、最小模板 fallback、單次模型呼叫、JSON 擷取與可用工具驗證。`IntentParserWorker` 保留讀取 Channel、選取上下文、開始／完成回合、回答生成及指令交接。
- 涉及檔案：`src/IntentRouter.cs`、`src/IntentParserWorker.cs`、`src/Program.cs`。
- 驗證指令與結果：依使用者目前記憶體限制，未執行建置、測試或主程式；已靜態確認 Router 的工具目錄與 JSON 解析已不在 Worker 中，DI 已註冊 `IntentRouter`。
- 實機驗證與環境：尚未執行；需以一般對話、合法工具、未知工具、缺少工具欄位及不合法 JSON 驗收既有降級行為。
- 未完成項目或限制：未新增 `ConversationProcessor`、Session、TurnId 或新 Channel；目前 Worker 的單輪協調職責仍清楚，沒有拆分必要。
- 計畫偏離與原因：依防止過度設計準則，P3 只抽出已有明確邊界的 Router，不強制建立完整協調層。
- 下一步：等待可用環境補做 P1～P3 驗證；後續 P4 以最小轉接統一現有工具入口。

### 2026-09-16／P4：統一工具執行入口

- 修改摘要：新增 `ToolExecutionService`，統一內建 `IMcpTool` 與 DLL `IAgentTool` 的執行結果、TTS 政策與記憶摘要。`McpToolExecutor` 不再直接依工具來源分派或處理 DLL 載入例外。外部工具執行前檢查 manifest 必填參數；Host 啟動與工具目錄列舉仍不載入 DLL。
- 涉及檔案：`src/Tools/ToolExecutionService.cs`、`src/McpToolExecutor.cs`、`src/Program.cs`、工作清單。
- 驗證指令與結果：依使用者目前記憶體限制，未執行建置、測試或主程式。靜態檢查確認 `McpToolExecutor` 已不直接依賴 `IMcpTool`、`ToolManifestCatalog` 或 `LazyDllToolLoader`，且 `file_search` 的 `SpeakResult=false`、`MemorySummary` 流程仍由統一結果保留。
- 實機驗證與環境：尚未執行；需驗收內建時間工具、file_search、缺少 `fileName`、未知工具、DLL 首次載入、損壞新版回退、取消及非 TTS 回合完成順序。
- 未完成項目或限制：內建工具尚未補齊 JSON Schema 或描述，Router 的既有內建工具 Prompt 不在此階段改寫；不修改 DLL 公開契約、延遲載入器或插件 manifest 格式。
- 計畫偏離與原因：為維持最小範圍，僅統一執行入口與結果政策；未建立通用 Registry、Factory 或新插件平台。
- 下一步：P5 執行靜態整合檢查，執行期驗收延後。

### 2026-09-16／P5：靜態整合檢查與交接

- 修改摘要：檢查 P1～P4 的責任邊界與相依方向，確認輸入／輸出 Channels、SQLite、DLL Loader、STT、TTS 與既有工具註冊仍保留。更新工作清單及本施工紀錄，列出待執行驗收。
- 驗證指令與結果：先前執行的 Prompt 預算測試為 4/4 通過，檔案搜尋回歸為 12/12 通過；使用者於 2026-09-16 完成本機 Solution 建置與功能驗收，確認功能正常。
- 實機驗證與環境：本機功能驗收已由使用者完成。
- 未完成項目或限制：未逐項保存 Router、取消、長上下文與音訊設備的測試輸出；日後若調整相關功能，需重新執行對應驗收。
- 下一步：本施工計畫結案；依新功能需求建立後續工作。
