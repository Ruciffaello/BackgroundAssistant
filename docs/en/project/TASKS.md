# BackgroundAssistant Tasks & Roadmap

This document is the single source of truth for development tasks and work status. The section where a task resides represents its current status.

Standard items record "Description" and "Acceptance Criteria". Next steps, blockers, or verification results are added when work starts, blocks, or finishes.

## In Progress

### FEAT-002 Validate Conversation Turns & BM25 Context (V1)

- **Description**: Persist full turns for user `local-default` in SQLite; select relevant context from the last 2 turns using BM25 before answering.
- **Acceptance Criteria**: Shared CMD and STT pipeline; irrelevant turns excluded from Prompt; relevant turns continue context; respects token budget.
- **Out of Scope**: Long-term memory extraction, Profile, sensitive data confirmation, embeddings, vector databases, TurnId, or voiceprints.
- **Specification**: [User Memory Design (V1)](../design/USER_MEMORY_DESIGN.md).
- **Verification**: [User Memory Verification Checklist](../testing/USER_MEMORY_VERIFICATION.md).
- **Completed**: 4-table migrations, full turn persistence, last 2 turns retrieval, character bigram BM25, configurable thresholds, and score logging.
- **Status**: Build succeeded; migration idempotency and smoke tests passed. Real-device BM25 scenarios pending.
- **Next Steps**: Validate false positives/negatives using [BM25 Test Scenarios](../testing/BM25_TEST_SCENARIOS.md) and calibrate `MinimumBm25Score`.

### FEAT-003 Stabilize Single-Pass Conversation / Tool Router

- **Description**: Default to conversation; only explicit tool requests generate tool names and parameters in the same LLM inference step.
- **Acceptance Criteria**: General dialogue routes to `conversation`; time, news, cards, and system shutdown route to respective tools; invalid JSON safely falls back to conversation.
- **Completed**: Removed 6-action routing and second Tool Planner; added `mode`, `subject`, and flat tool parameters.
- **Status**: Configuration JSON valid and solution builds cleanly; real-device regression pending.

## Next

### FEAT-004 Redefine Minimal Long-Term Memory Workflow

- **Description**: `MemoryItems` currently exists only as a table without extraction, persistence, or search behavior.
- **Acceptance Criteria**: Define data sources, persistence criteria, sensitive data policy, and query patterns prior to implementation; do not let conversation history implicitly expand into long-term memory.

## Pending

### TECH-001 Establish Repeatable Verification Pipeline

- **Description**: Core business and plugin logic now have automated unit tests; expand into end-to-end and CI automation pipelines.
- **Acceptance Criteria**: Executable automated build and test pipeline with repeatable verification.

### TECH-002 Complete Local Tool Implementation & Calibration

- **Description**: Some local tools remain simulated or incomplete; calibrate behavior based on actual requirements.
- **Acceptance Criteria**: Target tools pass error handling and functional verification tests.

### TECH-003 Configuration & Secret Management Cleanup

- **Description**: Model paths and execution parameters are hardcoded; sensitive settings should be externalized from Git tracking.
- **Acceptance Criteria**: Environment settings supplied externally; Git tracking contains no secrets.

### FEAT-001 Add Voice Wake Word Mechanism

- **Description**: Add wake word activation to prevent ambient audio from triggering pipelines.
- **Acceptance Criteria**: Wake conditions, resource limits, and voice integration verified.

## Deferred

### TECH-005 Bi-Directional MCP & Partner Tool Integration

- **Description**: Target dual MCP Client/Server capabilities to integrate partner tools; currently deferred.
- **Reassessment**: When onboarding the first partner, external tool requirement, or exposing outbound capabilities.
- **Reference**: [MCP Integration Gap Report](../research/MCP_INTEGRATION_GAP.md), [DEC-002](DECISIONS.md#dec-002-dual-mcp--dll-plugin-product-positioning).

## Recently Completed

### TECH-004 Design & Implement DLL Tool Plugin Mechanism

- **Description**: Enable dynamic extension of local tools via lazy-loaded DLL plugins integrated into a unified catalog.
- **Completed**: 2026-08-25.
- **Deliverables**:
  - `BackgroundAssistant.PluginContracts`: Defines `IAgentTool`, `ToolDescriptor`, `ToolResult`.
  - `BackgroundAssistant.PluginRuntime`: Implements `ToolManifestCatalog` and `LazyDllToolLoader` (SHA-256 fingerprinting, shadow copying, collectible ALC, corrupted version fallback).
  - `BackgroundAssistant.FileSearchTool`: Implements whole-disk filename search based on `ripgrep`, supporting exact-match priority, substring fallback, UTF-8/special characters, and system folder permission skips.
  - `FileSearchTool.Tests`: 12 automated unit tests.
- **Verification**: 12/12 unit tests passing, whole-disk search verified on real machine.

### TECH-006 Implement Lightweight Documentation Management

- **Description**: Created project overview, unified tasks list, architectural decision records, and organized research archives.
- **Completed**: 2026-08-21.
- **Verification**: Documentation responsibilities separated; project roadmap accessible from root `README.md`.
