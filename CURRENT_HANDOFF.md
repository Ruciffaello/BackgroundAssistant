# BackgroundAssistant 目前工作狀態

> 更新日期：2026-08-25  
> 工作目錄：`D:\C#\20260505_mcp\BackgroundAssistant`  
> 用途：關閉 Codex 或換新工作階段後，先閱讀此文件再繼續工作。

## 一、目前目標

為 BackgroundAssistant 加入可延遲載入的 DLL Tool。第一個外部工具是 `file_search`，使用開源的 `ripgrep`（`rg`）依照檔名搜尋電腦中的檔案。

設計原則：

- Host 啟動時只讀取 `plugin.json`，不載入 Tool DLL。
- Router 決定呼叫工具後，才檢查並透過 Reflection 載入 DLL。
- DLL 沒有改變時重用既有實例。
- DLL 更新後，在下一次呼叫時載入新版。
- 新版損壞時保留上一個已成功載入的版本。
- 不使用 `FileSystemWatcher`、背景熱載入或完整 Plugin 狀態機。

## 二、目前已完成

### 1. FileSearch DLL

已建立獨立專案：

```text
src/PluginContracts/
src/PluginRuntime/
src/Plugins/FileSearch/
tests/FileSearchTool.Tests/
```

FileSearch 已具備：

- 使用 `rg --files -uuu --no-config` 搜尋檔名。
- 先回傳完整檔名相符；沒有完整結果才回傳包含結果。
- 不分大小寫比對。
- 支援中文、空白及 glob 特殊字元。
- 使用 `ProcessStartInfo.ArgumentList`，不經過 shell。
- 預設總逾時 15 秒。
- 最多顯示 20 筆。
- 支援 `CancellationToken` 並終止 `rg` 程序樹。
- 結果只顯示，不送進 TTS。
- 最近對話只保存「找到 N 個結果」摘要，不保存完整路徑。

### 2. DLL 契約

`BackgroundAssistant.PluginContracts` 定義：

```text
IAgentTool
ToolDescriptor
ToolResult
```

Reflection 只負責尋找入口型別及建立物件；正式執行使用 `IAgentTool.ExecuteAsync`，不使用 `MethodInfo.Invoke`。

### 3. Manifest Catalog

`ToolManifestCatalog` 在 Host 啟動時掃描：

```text
plugins/*/plugin.json
```

它只讀取工具名稱、描述、必要參數、入口 DLL 和入口型別，不會載入 DLL。

建置 FileSearch 專案後會自動部署：

```text
plugins/file_search/
  plugin.json
  BackgroundAssistant.FileSearchTool.dll
```

`plugins/` 是執行期產物，已由 `.gitignore` 排除。

### 4. 呼叫時載入

`LazyDllToolLoader` 已完成：

1. 每次呼叫計算來源 DLL 的 SHA-256。
2. 首次呼叫或指紋改變時，複製 DLL 到 `.plugin-cache/`。
3. 使用 collectible `AssemblyLoadContext` 與 Reflection 建立 Tool。
4. `BackgroundAssistant.PluginContracts` 固定使用 Host 的共用組件。
5. 從資料流載入影子副本，避免 Windows 鎖住 DLL 檔案。
6. 同一工具使用 `SemaphoreSlim` 避免重複載入與同時切換。
7. 損壞新版不會取代已載入的舊版。

### 5. Router 與 Executor 串接

Router 現在接受：

```json
{
  "mode": "tool",
  "subject": "簡歷.pdf",
  "tool": "file_search",
  "fileName": "簡歷.pdf"
}
```

`IntentParserWorker` 的可用工具名稱為：

```text
內建 IMcpTool + plugin.json 中的外部工具
```

`McpToolExecutor` 的執行順序：

1. 先尋找既有內建 `IMcpTool`。
2. 找不到時查詢 DLL Tool Catalog。
3. 呼叫 `LazyDllToolLoader`。
4. 依 `SpeakResult` 決定是否進入 TTS。
5. FileSearch 不進 TTS，直接將系統恢復為 IDLE。

### 6. Router Token 超限修正

曾發生：

```text
Prompt template exceeds the 1024-token context budget
```

已修正：

- Router System Prompt 從約 703 字縮短至 293 字。
- Router User Template 從約 826 字縮短至 521 字。
- 外部工具目錄不再加入完整 JSON Schema，只列工具名稱與必要參數。
- 如果 few-shot 模板仍超過 token 預算，自動改用無範例的最小模板。
- 單次 Router 失敗不再終止整個 `IntentParserWorker`。

## 三、目前驗證結果

執行：

```powershell
dotnet run --project tests\FileSearchTool.Tests\BackgroundAssistant.FileSearchTool.Tests.csproj
```

結果：

```text
12/12 通過
```

測試涵蓋：

- 完整檔名優先。
- 包含搜尋 fallback。
- 中文檔名。
- 特殊字元按字面搜尋。
- 最大結果數。
- CancellationToken。
- 顯示與記憶摘要策略。
- 找不到 `rg`。
- 第一次呼叫才 Reflection 載入 DLL。
- 損壞新版不取代已載入版本。
- 全磁碟搜尋存在檔案可正常回傳結果。
- 全磁碟搜尋不存在檔案正常回傳未找到。

Solution 建置：

```powershell
dotnet build BackgroundAssistant.sln --no-restore -m:1
```

目前結果：`0 errors`。

## 四、已修復之問題：rg 搜尋整顆磁碟回傳 Exit Code 2

### 問題原因：
Windows 環境下搜尋整顆磁碟根目錄（如 `C:\`、`D:\`）時，ripgrep 必然會遍歷到受 Windows 保護之系統目錄（如 `C:\System Volume Information`、`C:\ProgramData\...\SystemData`、`C:\PerfLogs` 等）並遇到「存取被拒 (os error 5)」。
ripgrep 的設計是在遇到任何 IO/權限錯誤時，即使正常列舉了其他所有目錄，也會在結束時將 Exit Code 設為 2。
原先 `RipgrepFileSearcher` 只要 Exit Code > 1 即拋出異常，導致全磁碟搜尋必定中斷報錯。

### 修復方式：
1. 更新 [`RipgrepFileSearcher.cs`](file:///D:/C%23/20260505_mcp/BackgroundAssistant/src/Plugins/FileSearch/RipgrepFileSearcher.cs)：容許非致命的受保護目錄略過。只有在未找到候選項且 stderr 帶有實質致命/語法錯誤時才視為失敗。
2. 若找到檔案，正常回傳結果；若全磁碟皆無該檔案，正常回傳「找不到檔名符合『...』的檔案」。
3. 新增全磁碟存在與不存在檔案的單元測試並全數驗證通過。

## 五、下一次開工順序

1. 檔案搜尋功能已由實機（`CURRENT_HANDOFF.md`）與單元測試（12/12）驗證通過。
2. 檢查 `git status`，確認所有新增的 Plugin 專案、測試與修改。
3. 執行 Git Commit 與 Push。
4. 繼續進行後續排定功能（如 BM25 門檻實機調優或長期記憶最小流程設計）。

## 六、目前 Git 狀態

這批 DLL Tool 相關變更尚未提交。最後已存在的 commit：

```text
3476782 docs: organize project documentation
```

目前修改／新增範圍：

```text
.gitignore
BackgroundAssistant.csproj
BackgroundAssistant.sln
appsettings.json
prompts.json
src/IntentParserWorker.cs
src/McpToolExecutor.cs
src/Program.cs
src/PluginContracts/
src/PluginRuntime/
src/Plugins/
tests/FileSearchTool.Tests/
CURRENT_HANDOFF.md
```

不要在未檢查差異前使用 `git reset --hard` 或覆蓋這些檔案。

## 七、快速恢復指令

重新開啟 Codex CLI 並續接最近工作階段：

```powershell
& "$env:APPDATA\npm\codex.cmd" resume --last
```

若進入全新 Codex 對話，第一句可使用：

```text
請先閱讀根目錄 CURRENT_HANDOFF.md、檢查 git status，然後告訴我目前進度與下一步。
```
