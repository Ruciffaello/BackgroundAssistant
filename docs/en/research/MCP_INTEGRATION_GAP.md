# MCP Integration Gap Analysis

> Purpose: Clarify differences between the current implementation and the target Model Context Protocol (MCP) architecture.
> Reference Date: 2026-08-21; MCP Specification Revision: 2025-11-25.

## 1. Executive Summary

BackgroundAssistant is currently an **in-process tool prototype**, not yet a standard MCP Client or MCP Server.

Current `IMcpTool` and `McpToolExecutor` classes provide in-process dispatching: `IntentParserWorker` produces structured JSON, and `McpToolExecutor` invokes matching `IMcpTool` instances registered in the DI container. While conceptually similar to MCP Tools primitives, standard JSON-RPC 2.0 and MCP protocol transports are not yet implemented.

Product Target: **A collaborative assistant platform supporting bi-directional MCP capabilities alongside local DLL plugins**:

- **MCP Client**: Discover and execute tools hosted by partner or third-party MCP Servers.
- **MCP Server**: Expose internal local capabilities to external MCP hosts/clients.
- **DLL Plugins**: Load high-performance in-process plugins on the local machine.
- **Unified Catalog**: Coordinate built-in tools, DLL plugins, and remote MCP servers through a single planner.

## 2. Current vs. Standard MCP Comparison

| Dimension | Current Implementation | Standard MCP Spec | Gap / Future Requirement |
| --- | --- | --- | --- |
| System Role | In-process parser, dispatcher & tools | Host managing multiple MCP Clients; optional MCP Server capability | Establish protocol boundaries and lifecycle isolation |
| Protocol Format | Channel strings & flat JSON | UTF-8 JSON-RPC 2.0 requests, responses, notifications | Implement JSON-RPC message layer or adopt official C# SDK |
| Lifecycle | Tied to .NET Host process | `initialize` -> capability negotiation -> `notifications/initialized` -> operation -> shutdown | Connection handshake, session negotiation, and graceful shutdown |
| Transport | In-process `System.Threading.Channels` | stdio or Streamable HTTP | stdio for local subprocesses; Streamable HTTP for remote services |
| Tool Discovery | Static registration in `Program.cs` | Dynamic client discovery via `tools/list` | Dynamic catalog merging across built-in, plugin, and remote sources |
| Schema & Descriptions | C# interface with `Name` property | Name, description, `inputSchema`, output annotations | Formal JSON Schema per tool for LLM reasoning |
| Error Model | String error messages | Protocol errors vs. Tool execution errors | Structured error codes and user-visible messages |
| Cancellation | Manual cancellation handling | Standard request timeout and cancellation tokens | Pass `CancellationToken` throughout execution pipeline |

## 3. Target Architecture

```text
User / Voice / CMD Input
          │
          ▼
BackgroundAssistant Host
          │
          ▼
Unified Tool Catalog & Dispatcher
   ├─ Built-in Adapter ──▶ Built-in Tools
   ├─ Plugin Adapter  ───▶ Local DLL Plugins
   └─ MCP Client Layer ──▶ Partner / Third-Party MCP Servers
          │
          └─ MCP Server Layer ──▶ Outbound Authorized Tools
```
