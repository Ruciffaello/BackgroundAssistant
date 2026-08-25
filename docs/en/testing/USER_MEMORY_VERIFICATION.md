# Conversation Data & BM25 Verification Checklist

## Usage

- This checklist verifies SQLite conversation persistence and BM25 Context retrieval in V1. Long-term memory items are reserved for future phases.
- Each item must be verifiable via visible output or log evidence.

## 1. Startup & Database Schema

- [ ] Automatically creates `agent_memory.db` and migrations on first startup.
- [ ] Subsequent boots do not recreate tables or corrupt existing records.
- [ ] Migration checks are idempotent.
- [ ] Active user defaults to `local-default`.
- [ ] Database contains no unused V1 structures (TurnId, voiceprints, profile histories).

## 2. CMD & STT Input Pipelines

- [ ] CMD inputs route through Router, answering/tool execution, and SQLite turn persistence.
- [ ] STT inputs pass through speech refiner and share identical downstream pipelines with CMD.
- [ ] Both CMD and STT write to user `local-default`.
- [ ] STT failure or empty strings do not create empty database records.

## 3. Recent Turn BM25 Context

- [ ] At most 2 historical turns retrieved per turn.
- [ ] BM25 score and `included` boolean logged for each candidate.
- [ ] Unrelated topics (Tokyo Time -> Chocolate) are excluded from context.
- [ ] Relevant topics with overlapping bigrams are included in context.
- [ ] Older turns (> 2 turns) are never included in context.
- [ ] Total prompt length respects the 1024-token budget.

## 4. Regression & Stability

- [ ] General queries output `conversation`; explicit tool queries output `tool`.
- [ ] Router JSON includes `subject`.
- [ ] Invalid JSON safely defaults to `conversation` without throwing unhandled exceptions.
- [ ] DLL plugin tools (`file_search`) load on demand and output results properly.
- [ ] Audio and ONNX resources release cleanly upon shutdown.
