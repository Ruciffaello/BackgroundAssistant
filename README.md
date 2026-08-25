# BackgroundAssistant

[English](README.md) | [繁體中文](README.zh-TW.md)

This document is the project overview and documentation entry point. Detailed task tracking is unified in [TASKS.md](docs/en/project/TASKS.md).

## Project Overview

BackgroundAssistant is a local AI voice assistant built on .NET 10 Worker Service. The system uses `System.Threading.Channels` to connect voice/CMD input, text refinement, LLM routing, local tools, and TTS, running primary inferences completely locally on the user's machine.

The current architecture includes built-in `IMcpTool` and a lazy-loaded DLL plugin mechanism (`BackgroundAssistant.PluginRuntime`). Standard MCP Client/Server integration is planned for future phases.

## Current Architecture

```text
Voice Input -> RawText -> TextRefinerWorker --+
                                              +-> CleanText -> IntentParserWorker
CMD Input ------------------------------------+                  |
                                                                 +-- conversation (Default)
                                                                 |      +-> BM25 Context Filtering (Last 2 turns)
                                                                 |      `-> Conversation LLM
                                                                 `-- tool (Explicit Request)
                                                                        `-> McpToolExecutor
                                                                               |-- Built-in IMcpTool
                                                                               `-- External LazyDllToolLoader
                                                                                      `-> Via SpeakResult -> TTS / IDLE
```

Key Components:

- **Input**: `SpeechToTextWorker`, `ConsoleInputWorker`, `InputWorkerBase`.
- **STT**: SenseVoiceSmall ONNX with NAudio.
- **Refinement & Routing**: Shared Phi-3.5 ONNX model.
- **Routing**: Single-pass output of `conversation`/`tool` and `subject`. Tool mode directly outputs the tool name and flat parameters; dynamically loads external Manifest Catalog.
- **Conversation Context**: SQLite stores complete turns; the last two turns are evaluated with character bigram BM25 filtering before injecting into the prompt.
- **Tools**: `IMcpTool`, `ToolManifestCatalog`, `LazyDllToolLoader`, and `McpToolExecutor`.
- **Plugin Example**: `FileSearchTool` (whole-disk filename search powered by `ripgrep`).
- **TTS**: SherpaOnnx VITS with NAudio.

## Current Status

- **Completed Build & Unit Tests**: Single-pass conversation/tool routing, direct tool dispatching, conversation SQLite database, last 2 turns BM25 filtering, DLL lazy-loading with shadow copying, corrupted DLL fallback protection, whole-disk file searching (12/12 passing).
- **Completed Hardware Verification**: `file_search` tool verified on real disk search.
- **Pending Hardware Verification**: BM25 threshold fine-tuning, CMD/STT continuous conversation regression.
- **Not Yet Implemented**: Long-term memory extraction, `MemoryItems` write/search, User Profile, sensitive data confirmation, vector embeddings.
- **Known Limitations**: BM25 performs lexical keyword matching rather than semantic embeddings.

## Roadmap & Directions

### Now

- Verify routing and context filtering against [BM25 Test Scenarios](docs/en/testing/BM25_TEST_SCENARIOS.md).
- Calibrate score thresholds and Chinese tokenization after collecting actual BM25 run scores.

### Next

- Create automated tests for Router, BM25, and token budget constraints.
- Redefine minimal workflows for long-term memory persistence and queries.

### Later

- Voice wake word activation.
- Add more local DLL plugin tools.

### Deferred

- Standard MCP Client/Server and external partner tool integration.

## Documentation Index

- **Documentation Hub**: [docs/README.md](docs/README.md)
- **English Documentation**: [docs/en/README.md](docs/en/README.md)
- **繁體中文說明文件**: [docs/zh/README.md](docs/zh/README.md)

| Document | Purpose |
| --- | --- |
| [TASKS.md](docs/en/project/TASKS.md) | Single source of truth for task status |
| [DECISIONS.md](docs/en/project/DECISIONS.md) | Architectural & design decisions |
| [PARSER_REDESIGN.md](docs/en/design/PARSER_REDESIGN.md) | Current Router specifications |
| [USER_MEMORY_DESIGN.md](docs/en/design/USER_MEMORY_DESIGN.md) | Conversation data & future memory boundaries |
| [HANDOFF.md](docs/en/project/HANDOFF.md) | Latest development context & handoff log |
| [BM25_TEST_SCENARIOS.md](docs/en/testing/BM25_TEST_SCENARIOS.md) | Router & BM25 manual test scenarios |
| [TEST_GUIDE.md](docs/en/testing/TEST_GUIDE.md) | General manual testing guide |
| [CHANGELOG.md](CHANGELOG.md) | Version changelog |

---

Last Updated: 2026-08-25
