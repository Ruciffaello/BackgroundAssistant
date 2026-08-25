# DLL Plugin 熱抽換功能規劃

## 文件狀態

- 狀態：未來規劃（Deferred）
- 目前尚未實作
- 本文件只記錄設計方向，不代表現有版本已支援 DLL Plugin
- 商業授權、自動下載與付費驗證不在目前規劃範圍

## 背景與目標

未來 BackgroundAssistant 需要支援以外部 DLL 擴充工具功能。管理者可自行將 Plugin 放入指定目錄，主程式在不重新建置、最好也不重新啟動的情況下，偵測並載入新的工具。

主要目標：

- 新增 DLL 工具時不重新建置 BackgroundAssistant。
- 新增、更新或移除 Plugin 時不中止主程式。
- Agent 內建工具與外部 DLL 工具使用相同的工具描述與執行介面。
- Plugin 載入失敗時不影響既有工具與主程式運作。
- 新版 Plugin 驗證失敗時繼續保留舊版。
- 保留未來加入遠端 MCP Server、商業授權與簽章驗證的擴充空間。

## 目前不處理的功能

第一階段不實作：

- Agent 自動下載 Plugin。
- 線上購買與付款流程。
- 客戶帳號、授權伺服器與 entitlement 驗證。
- 機器綁定與授權到期處理。
- Plugin 商業簽章及發行者驗證。
- 自動從遠端更新 Plugin。

未來需要商業化時，可在 Plugin manifest 與載入驗證流程中加入上述能力，不應改變核心工具介面。

## 技術前提

專案已移除 Native AOT，未來可使用可回收的 `AssemblyLoadContext` 在執行期間載入與卸載 .NET DLL。

熱抽換功能必須在主程式首次支援 Plugin 時預先建立穩定的 Contract。未來 DLL 可以在不更新主程式的情況下載入，但如果 Plugin 要求主程式不支援的新 Contract 版本，仍必須拒絕載入或升級主程式。

## 整體架構

```text
Tool-capable Router／Selector
    |
    v
Tool Registry Snapshot
    |-- BuiltInToolProvider
    |     `-- Agent 內建工具
    |
    |-- HotPluginToolProvider
    |     `-- 外部 DLL Plugin
    |
    `-- RemoteMcpToolProvider（未來）
          `-- 遠端 MCP Server
```

對 LLM 而言，工具來源不影響使用方式。內建工具、DLL Plugin 與未來遠端 MCP 工具都應提供相同的名稱、說明及輸入 JSON Schema。

## 建議模組

```text
BackgroundAssistant.ToolContracts
  |-- IAgentPlugin
  |-- IPluginContext
  |-- ToolDescriptor
  |-- ToolResult
  `-- ContractVersion

BackgroundAssistant
  |-- PluginManager
  |-- PluginLoadContext
  |-- PluginManifest
  |-- HotPluginToolProvider
  |-- ToolRegistry
  `-- PluginDirectoryWatcher
```

`BackgroundAssistant.ToolContracts` 應是獨立且穩定的組件，由主程式和所有外部 Plugin 共同引用。Contract 應盡量小，避免暴露主程式內部類別或完整的 `IServiceProvider`。

## Plugin Contract 草案

參數與結果建議以 JSON 作為穩定邊界，降低主程式資料型別變更造成的相容性問題。

```csharp
public interface IAgentPlugin : IAsyncDisposable
{
    PluginDescriptor Descriptor { get; }

    ValueTask InitializeAsync(
        IPluginContext context,
        CancellationToken cancellationToken);

    IReadOnlyCollection<ToolDescriptor> GetTools();

    ValueTask<ToolResult> ExecuteAsync(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken);
}
```

工具描述草案：

```csharp
public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolRiskLevel RiskLevel);
```

統一結果草案：

```csharp
public sealed record ToolResult(
    bool Success,
    string Content,
    string? ErrorCode = null);
```

正式實作前仍需確認串流輸出、二進位內容、進度通知與取消行為是否需要納入 Contract v1。

## Plugin 目錄與 Manifest

不建議直接覆蓋正在執行的 DLL。Plugin 應使用獨立版本目錄：

```text
plugins/
  weather/
    current.json
    1.0.0/
      plugin.json
      WeatherPlugin.dll
      Dependency.dll
      plugin.ready
    1.1.0/
      plugin.json
      WeatherPlugin.dll
      Dependency.dll
      plugin.ready
```

`plugin.json` 最小內容：

```json
{
  "id": "weather",
  "version": "1.1.0",
  "contractVersion": 1,
  "entryAssembly": "WeatherPlugin.dll",
  "entryType": "WeatherPlugin.WeatherModule"
}
```

`current.json` 指定啟用版本：

```json
{
  "version": "1.1.0"
}
```

管理者應先完成所有檔案複製，最後才建立 `plugin.ready`。PluginManager 只有在看到完成標記後才允許載入，避免讀取複製到一半的 DLL。

也可先複製到 `plugins/.staging/`，完成後再將整個版本目錄移入正式位置。

## 熱載入與更新流程

```text
偵測 Plugin 目錄變更
    -> debounce，等待檔案穩定
    -> 讀取並驗證 plugin.json
    -> 檢查 Contract 版本
    -> 建立新的 collectible AssemblyLoadContext
    -> 載入 DLL 與相依組件
    -> 建立 Plugin 實例
    -> 初始化及健康檢查
    -> 成功後原子切換 Tool Registry
    -> 舊版本停止接收新請求（Draining）
    -> 等待舊請求完成
    -> Dispose 舊 Plugin
    -> Unload 舊 AssemblyLoadContext
```

任何新版載入步驟失敗時，都必須維持舊版本可用，不能先移除舊版再嘗試載入新版。

## Tool Registry 快照

每個請求開始時取得不可變的工具快照：

```csharp
public interface IToolRegistry
{
    ToolRegistrySnapshot GetSnapshot();
}

public sealed record ToolRegistrySnapshot(
    long Version,
    IReadOnlyDictionary<string, ToolDescriptor> Tools);
```

範例：

```text
請求 A 取得 Registry v10，使用 weather 1.0.0
PluginManager 載入新版並切換到 Registry v11
請求 B 取得 Registry v11，使用 weather 1.1.0
請求 A 完成後，weather 1.0.0 才能卸載
```

這可避免工具更新發生在請求執行途中，導致 Planner 與 Executor 使用不同版本。

## Plugin 狀態

每個 Plugin 建議具有以下狀態：

```text
Discovered
Loading
Active
Draining
Unloading
Failed
Disabled
```

- `Active`：可接受新請求。
- `Draining`：不接受新請求，等待既有工作完成。
- `Failed`：載入或初始化失敗，不加入 Registry。
- `Disabled`：管理者停用，不自動載入。

## 變更偵測

建議同時支援三種載入時機：

1. 主程式啟動時掃描現有 Plugin。
2. 使用 `FileSystemWatcher` 偵測執行期間的變更。
3. 提供手動 `ReloadPluginsAsync` 作為漏失事件或維護時的備援。

`FileSystemWatcher` 可能針對一次複製產生多個事件，也可能在大量變更時漏失事件，因此必須加入 debounce，且不能成為唯一的重新掃描機制。

## 卸載限制

`AssemblyLoadContext.Unload()` 是請求卸載，不保證 DLL 立即從記憶體消失。Plugin 必須遵守以下規則：

- 不留下背景執行緒或未取消的工作。
- 取消自己建立的 timer。
- 解除向主程式註冊的事件。
- 不把 Plugin 型別或實例存入全域靜態欄位。
- 正確實作 `IAsyncDisposable`。
- 不讓主程式長期保存 Plugin Assembly 中的反射物件。

如果 Plugin 未正確清理，舊版可能仍留在記憶體，但新版工具仍可切換使用。PluginManager 應記錄卸載失敗或逾時狀況。

## 執行安全與穩定性

即使暫不實作商業授權，第一版仍應具備基本保護：

- 工具名稱不可與既有工具重複，除非明確允許版本替換。
- 驗證輸入 JSON 是否符合工具 Schema。
- 每次工具執行具有 timeout 與取消權杖。
- Plugin 例外不能終止主 Worker。
- 有副作用的工具必須標示風險等級。
- Plugin 路徑必須限制在指定的 `plugins` 根目錄。
- 載入失敗應記錄 Plugin ID、版本與錯誤原因。

程序內 Plugin 與主程式具有相同程序權限，因此只能載入可信任的 DLL。若未來需要執行第三方或不可信任 Plugin，應改用獨立 Plugin Host 進行程序隔離。

## 與 Agent 決策流程的整合

DLL Plugin 載入成功後更新 Tool Registry。現行 Router 把工具描述寫在 Prompt；實作動態插件時必須由 Registry 動態產生可用工具描述，不能繼續維護固定工具清單。

```text
Input
    -> Decision Router
    -> Router／Selector 取得最新 Registry Snapshot
    -> 明確需要工具時選擇內建工具或 DLL Plugin 工具
    -> Tool Executor 執行
    -> 工具結果回到 Agent Context
    -> 產生回答或繼續下一輪決策
```

下一個請求會自然取得新版工具清單；已在執行中的請求繼續使用原有快照。

## 未來商業化擴充點

商業授權延後設計，但可預留以下 manifest 欄位而不在第一版啟用：

```json
{
  "publisher": "YourCompany",
  "requiredEntitlement": "weather-pro",
  "packageHash": "...",
  "signature": "..."
}
```

未來的驗證應插入「建立 AssemblyLoadContext 之前」，驗證失敗的 Plugin 不得載入。Agent 是否下載 Plugin 仍可保持為外部部署流程，不必成為主程式功能。

## 未來實作階段建議

1. 建立獨立的 `BackgroundAssistant.ToolContracts` 專案。
2. 定義 Contract v1 與相容性規則。
3. 建立 Tool Registry 與 BuiltInToolProvider。
4. 將現有 `IMcpTool` 遷移或包裝成統一工具介面。
5. 建立 PluginLoadContext 與啟動掃描。
6. 加入執行期間載入、Registry 原子切換及 Draining。
7. 加入 FileSystemWatcher 與手動重新掃描。
8. 建立範例 Plugin 與整合測試。
9. 驗證更新失敗回復、並行請求及 AssemblyLoadContext 卸載。
10. 最後再評估商業授權、簽章與發行流程。

## 驗收條件草案

未來實作完成時，至少應通過：

- 主程式執行中加入新 Plugin，下一個請求可使用新工具。
- 主程式執行中更新 Plugin，既有請求不受影響，新請求使用新版。
- 新版 Plugin 無法載入時，舊版仍可正常使用。
- 移除或停用 Plugin 後，不再出現在新的 Tool Registry 快照。
- Plugin 執行例外不會終止主程式。
- Plugin 相依 DLL 可從自己的版本目錄解析。
- Contract 版本不相容時能拒絕載入並輸出明確錯誤。
- 不需重新建置或重新啟動 BackgroundAssistant。
