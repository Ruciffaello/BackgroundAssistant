# BackgroundAssistant Documentation Index

[English](README.md) | [繁體中文](../zh/README.md)

The project root keeps only the high-level overview and version history; all project management, design, research, and testing documents are centralized in `docs/en/`.

## Project Management

| Document | Purpose |
| --- | --- |
| [Project Overview (En)](../../README.md) | Current status, architecture, and documentation entry point |
| [專案總覽 (繁中)](../../README.zh-TW.md) | Traditional Chinese project overview |
| [Tasks List](project/TASKS.md) | Active, next, and pending tasks |
| [Key Decisions](project/DECISIONS.md) | Long-term architectural decisions & reassessment criteria |
| [Handoff Record](project/HANDOFF.md) | Context and verification from the latest development session |
| [Changelog](../../CHANGELOG.md) | Released and unreleased version history |

## Current Design

| Document | Purpose |
| --- | --- |
| [Parser Redesign](design/PARSER_REDESIGN.md) | Specifications for Conversation / Tool Router |
| [User Memory Design](design/USER_MEMORY_DESIGN.md) | Implemented conversation history and future memory boundaries |

## Research & Proposals

| Document | Purpose |
| --- | --- |
| [MCP Integration Gap](research/MCP_INTEGRATION_GAP.md) | Gap analysis between local tools and standard MCP |
| [DLL Plugin Hot-Swap](research/FUTURE_DLL_PLUGIN_HOT_SWAP.md) | Architectural proposal for dynamic plugin extensions |

## Testing & Verification

| Document | Purpose |
| --- | --- |
| [General Testing Guide](testing/TEST_GUIDE.md) | Manual test guide for Startup, Tools, CMD, STT, and TTS |
| [BM25 Test Scenarios](testing/BM25_TEST_SCENARIOS.md) | Test cases for Router, Subject, and BM25 relevance filtering |
| [User Memory Verification](testing/USER_MEMORY_VERIFICATION.md) | Verification checklist for conversation database & memory |

## Documentation Organization Principles

- **Root**: Contains only `README.md` (English), `README.zh-TW.md` (Traditional Chinese), and `CHANGELOG.md`.
- `docs/en/project/`: Task tracking, architecture decision records (ADRs), and handoff logs.
- `docs/en/design/`: Active architecture and design specifications.
- `docs/en/research/`: Research notes, future proposals, and comparative analyses.
- `docs/en/testing/`: Repeatable test scenarios, guides, and verification checklists.
