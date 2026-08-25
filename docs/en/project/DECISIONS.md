# BackgroundAssistant Architecture Decision Records (ADRs)

This document records decisions that have long-term architectural, developmental, or strategic impact. Standard implementation details are preserved in code and Git history.

## DEC-001 Lightweight Documentation Management

- **Date**: 2026-08-21.
- **Decision**: Use root `README.md` for project overview, `TASKS.md` as the single source of truth for task status, `DECISIONS.md` for architecture decisions, and categorize design and research under `docs/design/` and `docs/research/`.
- **Rationale**: Maintain essential traceability while minimizing time spent discovering and maintaining documentation.
- **Maintenance Rule**: Record each piece of information once; write only what cannot be directly answered by code, Git, or automated tools.
- **Reassessment**: When team size or delivery processes exceed what this structure can support.

## DEC-002 Dual MCP & DLL Plugin Product Positioning

- **Date**: 2026-08-21.
- **Decision**: The product aims to possess both MCP Client and MCP Server capabilities, while supporting local DLL plugins for in-process extensions, and calling partner or third-party MCP Servers under authorization agreements.
- **Rationale**: The assistant needs to integrate built-in capabilities, local extensions, and remote partner tools under a unified catalog, rather than being strictly restricted to a single Client or Server role.
- **Status**: Currently an in-process local tool prototype. Standard MCP protocol and dynamic DLL plugin hot-swapping are not yet scheduled for the immediate sprint.
- **Impact**: Future requirements include a unified tool catalog, source adapters, bi-directional protocol boundaries, and independent external authorization policies.
- **Reassessment**: When designing plugin contracts or onboarding the first partner MCP Server.
- **Reference**: [MCP Integration Gap Report](../research/MCP_INTEGRATION_GAP.md).

## DEC-003 Persist Conversation Turns Prior to Long-Term Memory

- **Date**: 2026-08-21 (Revised 2026-08-24).
- **Decision**: SQLite currently manages only a fixed user and complete conversation turns; `MemoryItems` exists only as a table schema without active persistence or search logic. User Profile, MemoryWorker, and safety confirmation flows are not yet implemented.
- **Rationale**: Validate conversation continuity and database lifecycle first, preventing "memory" from prematurely ballooning into extraction, classification, overwrite, and vector platform complexity.
- **Scope**: 4 tables: `SchemaMigrations`, `Users`, `ConversationMessages`, `MemoryItems`. No Profile or Session tables.
- **Reassessment**: Once conversation context is stable, evaluate long-term memory write, sensitive data, and search rules incrementally.
- **Reference**: [User Memory Design (V1)](../design/USER_MEMORY_DESIGN.md).

## DEC-004 Default to Conversation; Intercept for Explicit Tools

- **Date**: 2026-08-24.
- **Decision**: The Router outputs only `conversation` or `tool`, along with a `subject`. Explicit tool requests generate tool names and parameters in the same single LLM inference step.
- **Fallback Strategy**: Invalid JSON, unknown modes, or unavailable tools fallback directly to general conversation without downgrading to `clarify`.
- **Rationale**: Eliminates overlapping 6-action classifications and prevents technical errors from masking user intent as clarification requests.
- **Impact**: Removes the second Tool Planner LLM pass; ambiguous requests are followed up naturally by the conversation model.
- **Reassessment**: When tool count exceeds what a single prompt can stably describe, or when adopting a dynamic Tool Registry.

## DEC-005 BM25 Filtering for Recent Conversation Turns

- **Date**: 2026-08-24.
- **Decision**: Compare current input against candidate user inputs from the last two turns using character bigram BM25; include the turn in the prompt only when the score meets the threshold.
- **Rationale**: Avoid prompt pollution from unrelated previous turns without introducing heavy embedding models, custom tokenizers, vector databases, or extra LLM inference stages.
- **Limitations**: BM25 performs lexical keyword matching rather than semantic understanding; pronouns and synonyms may be missed, while common generic words might trigger false positives.
- **Reassessment**: When real-device testing shows thresholds and tokenization cannot mitigate false positives or negatives.

## DEC-006 On-Demand Lazy Loading & Shadow Copying for DLL Plugins

- **Date**: 2026-08-25.
- **Decision**:
  1. **Lightweight Startup Scan**: Host scans only `plugins/*/plugin.json` via `ToolManifestCatalog` without loading tool DLLs.
  2. **On-Demand Loading**: `LazyDllToolLoader` computes SHA-256 fingerprint and loads DLL via reflection only when the tool is triggered.
  3. **Shadow Copying**: Copies DLL to `.plugin-cache/` and loads from a byte stream to prevent Windows OS file locking.
  4. **Collectible ALC & Fault Fallback**: Uses isolated collectible `AssemblyLoadContext`. If a new version is corrupted or fails to load, the system retains and falls back to the previously loaded instance for high availability.
- **Rationale**: Prevents tools from consuming memory and startup time at boot, and allows seamless compilation/deployment without file lock issues.
- **Reassessment**: When plugin count scales significantly or requires dynamic live unloading with complex lifecycles.
