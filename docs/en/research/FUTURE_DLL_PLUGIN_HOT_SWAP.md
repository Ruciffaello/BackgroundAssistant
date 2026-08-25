# Dynamic DLL Plugin Architecture & Hot-Swapping Proposal

## Status

- **Status**: Future Proposal (Deferred)
- Not yet implemented in V1.
- This document outlines architectural direction and does not imply current support.

## Context & Objectives

In future iterations, BackgroundAssistant should support dynamic extensibility through external DLL plugins placed in designated directories, allowing the host to detect and load tools without recompilation or application restarts.

Primary Objectives:
- Add new DLL tools without rebuilding BackgroundAssistant.
- Add, update, or remove plugins without terminating the host process.
- Shared descriptor and invocation interface between built-in tools and external plugins.
- Graceful isolation: plugin loading failures do not crash the host.
- Retain existing working instances if updated plugins fail verification.
- Extensibility for remote MCP servers, digital signatures, and licensing.

## Out of Scope for Phase 1

- Automated plugin marketplace downloads.
- Online payments, entitlement validation, and licensing servers.
- Machine binding and expiration handling.
- Plugin binary code signatures and publisher verification.

## Technical Foundation

The project does not use Native AOT, enabling collectible `AssemblyLoadContext` for runtime loading and unloading.

A stable `BackgroundAssistant.ToolContracts` assembly must be established. Future plugins can be loaded dynamically, but plugins targeting unsupported contract versions will be rejected.

## Proposed Architecture

```text
Tool-capable Router / Selector
    |
    v
Tool Registry Snapshot
    |-- BuiltInToolProvider  --> Built-in tools
    |-- HotPluginToolProvider --> External DLL plugins
    `-- RemoteMcpToolProvider --> Remote MCP servers (Future)
```

## Proposed Contracts

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

public sealed record ToolDescriptor(
    string Name,
    string Description,
    JsonElement InputSchema,
    ToolRiskLevel RiskLevel);

public sealed record ToolResult(
    bool Success,
    string Content,
    string? ErrorCode = null);
```

## Directory Structure & Manifest

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

`plugin.json`:

```json
{
  "id": "weather",
  "version": "1.1.0",
  "contractVersion": 1,
  "entryAssembly": "WeatherPlugin.dll",
  "entryType": "WeatherPlugin.WeatherModule"
}
```

## Hot Reload Lifecycle

```text
Detect directory change
  -> Debounce file writes
  -> Read and validate plugin.json
  -> Check contract version compatibility
  -> Instantiate new collectible AssemblyLoadContext
  -> Load DLL and dependencies
  -> Instantiate plugin and perform health checks
  -> Atomically swap Tool Registry snapshot
  -> Drain active requests on older plugin version
  -> Dispose and unload old AssemblyLoadContext
```
