# Conversation Data & Long-Term Memory Design Boundaries

## Implementation Status

As of 2026-08-24, only a fixed user (`local-default`), full conversation turn persistence, retrieval of the last two turns, and BM25 relevance filtering are implemented. `MemoryItems` is not yet populated or queried; User Profile, MemoryWorker, sensitive data confirmation, forget/clear, and retention expiration are not yet implemented. Sections marked as "Future Rules" describe planned behavior rather than current features.

## Goals

The current phase addresses only two requirements:

1. Persisting complete user/assistant conversational turns.
2. Injecting only recent turns with lexical relevance to the current input before generating answers.

This document serves as the implementation baseline for V1 user memory. In case of conflict with other proposals, this document takes precedence.

## Overall Workflow

```text
STT Input -> Speech-to-Text & Refiner --+
                                        +-> Router
CMD Input ------------------------------+     |-- conversation -> BM25 last 2 turns -> LLM Answer
                                              `-- tool -> McpToolExecutor
                                                          |
                                                          `-> Write completed turn to SQLite
```

- STT and CMD share the identical downstream pipeline once clean text is obtained.
- Existing Channel and `GlobalStateService` handle input sequencing; V1 does not introduce `TurnId`.
- There is currently no background memory analysis or `MemoryJob`.

## V1 Components

Currently implemented:

- `AgentMemoryDatabase`: Migrations, fixed `local-default` user, full turn persistence, and recent turn retrieval.
- `RecentConversationService`: Retrieves the last two turns and constructs relevant context.
- `Bm25RelevanceScorer`: Scores current input against candidate previous user inputs.

No unused Identity Resolvers, Repositories, MemoryWorkers, or Policy abstractions have been created.

## SQLite Schema

Phase 1 uses an isolated `agent_memory.db` with four tables:

| Table | Purpose |
| --- | --- |
| `SchemaMigrations` | Database version and migration history |
| `Users` | Basic user identity; currently only `local-default` |
| `ConversationMessages` | Completed user/assistant turns |
| `MemoryItems` | Plain-text items reserved for future explicit memories |

`ConversationMessages` stores completed turns. Up to two recent turns are retrieved before answering; BM25 is computed between current input and each candidate `UserText` (using Chinese bigrams and excluding common query stopwords such as "什麼", "怎麼", "如何", "請問", "知道"). Identical user inputs or repeated assistant outputs are filtered out. Only turns meeting `MinimumBm25Score` are appended to the prompt with both user and assistant text. `MemoryItems` is not yet connected to persistence or query flows. Profile and Session tables do not exist.

## Long-Term Memory & Safety Rules (Future Rules, Not Yet Implemented)

If long-term memory is added in the future, extraction and safety policies must be explicitly confirmed. The current Router outputs no memory tags and contains no `None`/`Likely`/`Explicit`/`Forbidden` classifications:

- `None`: No memory requirement detected.
- `Likely`: Possible stable preference or personal profile detail.
- `Explicit`: User explicitly requested to remember.
- `Forbidden`: Suspected prohibited data.

Similarly, the following policy outcomes are planned directions; current code contains no `MemoryWorker` or `MemoryPolicy`:

- `Allow`: General preferences or stable facts that can be stored directly.
- `RequireConfirmation`: Sensitive data (health, finances) requiring explicit user confirmation first.
- `Reject`: Passwords, ID numbers, OTPs, secret keys, and other highly sensitive data that must never be stored.

V1 retains at most one pending confirmation in memory at a time and does not create a `PendingProfileChanges` table. Discarding pending items on restart is acceptable.

### Memory Conflict & Updates

V1 will adopt the principle: "Overwrite only when explicit, merge when compatible, ignore when ambiguous":

- `Replace`: User explicitly corrects previous facts or states changes; updates existing data without maintaining version history.
- `Merge`: New data is compatible with existing facts; merges and deduplicates.
- `Ignore`: Conflict or ambiguity without explicit correction; retains existing facts, new content remains only in conversation logs.
- Sensitive data meeting `Replace` or `Merge` must still undergo `RequireConfirmation`.
- Prohibited data is always rejected (`Reject`).

If conflicting facts impact the immediate answer, the Agent may ask the user for clarification; otherwise, it should not interrupt conversation purely to complete profile records.

### Retention & Deletion Lifecycle (Proposal, Not Implemented)

- `UserProfiles` and `MemoryItems` persist indefinitely until user requests modification or deletion.
- `ConversationMessages` are retained for the last 30 days.
- Empty `ConversationSessions` can be deleted after message purging.
- Expired cleanup runs once at startup; no background cron job in V1.
- No default DB storage quota initially; growth will be monitored first.

When a user requests to "forget" information:

1. Delete corresponding structured profile fields and `MemoryItems`.
2. Delete explicitly identifiable conversational messages.
3. If unable to reliably locate specific messages, prompt user to clear current session or entire history; avoid fuzzy mass deletion.

## Agent Personality (Future Proposal, Not Implemented)

Agent personality is separated into `agent_profile.json`, isolated from user profile data:

- `core`: Immutable constraints that the Agent cannot modify.
- `personality`: Personality settings that the Agent can suggest modifications for.

Workflow: "Agent proposal -> User confirmation -> Schema validation -> Atomic write with backup". The Agent may not rewrite itself without confirmation, nor may it write user preferences back to global agent settings.

## Deliberately Excluded from V1

The following items are deferred to avoid premature abstraction:

- `TurnId`
- Voiceprint tables and speaker identification
- Granular tables (`UserTraits`, `UserInterests`, `HealthRecords`, `Relationships`)
- Dedicated Repository per data entity
- Multiple granular Policy classes
- `PendingProfileChanges` persistence
- Complete profile edit history with `IsActive` version chains
- Fixed 8-turn periodic summarization (summarize only under token pressure)
- Embeddings, vector indexing, semantic RAG (only lexical BM25 is currently used)
- Profile query tools
- Multi-user concurrent writes and distributed locking
- Database encryption and commercial licensing

## Acceptance Criteria for Current Phase

- CMD and STT share Router, answering, tools, and turn persistence flows.
- Completed turns are restored across application restarts.
- Up to 2 recent turns are filtered individually via BM25; irrelevant context is excluded from prompts.
- Relevant context injection respects the model token budget.
- Long-term memory, profile, and safety policies are accurately documented as not yet implemented.

See [User Memory Verification Checklist](../testing/USER_MEMORY_VERIFICATION.md) for full test cases.
