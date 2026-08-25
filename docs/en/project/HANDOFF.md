# BackgroundAssistant Handoff Document

> Date: 2026-08-25  
> Working Directory: `D:\C#\20260505_mcp\BackgroundAssistant`  
> Purpose: Read this document upon starting a new session before proceeding with work.

## 1. Current Goal

Integrate lazy-loaded DLL tools into BackgroundAssistant. The first external plugin is `file_search`, which uses open-source `ripgrep` (`rg`) to search files by name across local drives.

Design Principles:

- Host scans only `plugin.json` at startup; tool DLLs are not loaded.
- DLLs are loaded via Reflection only after the Router decides to execute the tool.
- Reuse loaded instances if the DLL has not changed.
- Load new versions on the subsequent call when a DLL is updated.
- Retain previously loaded working version if a new version is corrupted.
- Avoid `FileSystemWatcher` or complex hot-reload state machines in V1.

## 2. Completed Work

### 1. FileSearch DLL

Created independent projects:

```text
src/PluginContracts/
src/PluginRuntime/
src/Plugins/FileSearch/
tests/FileSearchTool.Tests/
```

FileSearch features:

- Uses `rg --files -uuu --no-config` to locate filenames.
- Exact filename match first; falls back to substring match if no exact match is found.
- Case-insensitive comparison.
- Supports Chinese, whitespace, and special characters.
- Uses `ProcessStartInfo.ArgumentList` directly without shell invocation.
- Default 15-second timeout.
- Maximum 20 results displayed.
- Supports `CancellationToken` to terminate the `rg` process tree.
- Results are displayed in text and excluded from voice TTS.
- Context stores concise summary ("Found N files") instead of entire path listings.

### 2. DLL Contracts

`BackgroundAssistant.PluginContracts` defines:

```text
IAgentTool
ToolDescriptor
ToolResult
```

Reflection handles entry point discovery and instantiation; execution uses `IAgentTool.ExecuteAsync` without `MethodInfo.Invoke`.

### 3. Manifest Catalog

`ToolManifestCatalog` scans `plugins/*/plugin.json` at boot time without loading DLLs.

Building the FileSearch project automatically outputs:

```text
plugins/file_search/
  plugin.json
  BackgroundAssistant.FileSearchTool.dll
```

(`plugins/` is an execution output ignored by `.gitignore`.)

### 4. On-Demand Loading

`LazyDllToolLoader`:

1. Computes SHA-256 fingerprint of the source DLL on each invocation.
2. Copies DLL to `.plugin-cache/` on initial load or fingerprint change.
3. Loads via collectible `AssemblyLoadContext` and Reflection.
4. Uses Host's shared `BackgroundAssistant.PluginContracts`.
5. Loads from memory stream shadow copy to prevent Windows file locking.
6. Synchronized via `SemaphoreSlim` per tool to prevent race conditions.
7. Corrupted new versions do not overwrite previously working loaded instances.

### 5. Router & Executor Integration

Router outputs:

```json
{
  "mode": "tool",
  "subject": "簡歷.pdf",
  "tool": "file_search",
  "fileName": "簡歷.pdf"
}
```

`IntentParserWorker` available tools: Built-in `IMcpTool` + external tools from `plugin.json`.

`McpToolExecutor` resolution:
1. Search built-in `IMcpTool`.
2. Query DLL Tool Catalog.
3. Invoke `LazyDllToolLoader`.
4. Dispatch to TTS or IDLE according to `SpeakResult`.

### 6. Router Token Budget Optimization

Fixed previous context overflow errors:
- Router System Prompt shortened from ~703 chars to 293 chars.
- Router User Template shortened from ~826 chars to 521 chars.
- Catalog includes only tool names and required parameters instead of full JSON schema.
- Automatic fallback to minimal zero-shot template if few-shot template exceeds budget.
- Router failure no longer crashes `IntentParserWorker`.

## 3. Verification Results

Run tests:

```powershell
dotnet run --project tests\FileSearchTool.Tests\BackgroundAssistant.FileSearchTool.Tests.csproj
```

Result: `12/12 Passed`.

Solution build:

```powershell
dotnet build BackgroundAssistant.sln --no-restore -m:1
```

Result: `0 errors`.

## 4. Resolved Issue: Ripgrep Exit Code 2 on Full Disk Scans

### Root Cause:
Scanning root drives (`C:\`, `D:\`) encounters Windows protected folders (`System Volume Information`, `PerfLogs`, etc.) returning OS error 5 (Access Denied). Ripgrep sets Exit Code to 2 when IO/permission errors occur even if all accessible files are scanned.

### Fix:
Updated `RipgrepFileSearcher.cs` to tolerate non-fatal directory skips. Returns results normally if files are found, or returns "No files found" cleanly if none match.

## 5. Next Steps

1. Review `git status` for new plugins, tests, and documentation.
2. Commit and push changes.
3. Proceed with scheduled tasks (BM25 threshold calibration, long-term memory minimal flow).
