# Conversation Router & BM25 Test Scenarios

This document contains manual test cases for:

- `conversation` vs. `tool` routing
- `subject` extraction
- BM25 exclusion of irrelevant historical turns
- BM25 retention of relevant historical context
- Lexical limitations of BM25 matching

## Pre-test Notes

Application restarts do not wipe the SQLite database; BM25 evaluates the most recent two turns. Review `[2. BM25 Context]` console logs during testing to verify which turns are being scored.

Configuration in `appsettings.json`:

```json
"ConversationRelevance": {
  "MaxTurns": 2,
  "MinimumBm25Score": 0.25
}
```

Candidate log format:

```text
[2. BM25 Context]: score=0.000, included=False, text=Candidate user input
```

## Scenario 1: Eliminate Tokyo Time Context Pollution

Input 1:
```text
現在東京幾點？ (What time is it in Tokyo?)
```

Expected Router:
```json
{
  "mode": "tool",
  "subject": "東京時間",
  "tool": "get_time",
  "location": "Tokyo"
}
```

Input 2:
```text
你喜歡吃巧克力嗎？ (Do you like eating chocolate?)
```

Expected Router:
```json
{
  "mode": "conversation",
  "subject": "assistant"
}
```

Expected BM25:
```text
[2. BM25 Context]: score=0.000, included=False, text=現在東京幾點？
```

The final answer must not mention Tokyo, Japan, or time.

## Scenario 2: Retain Explicitly Relevant Context

Input 1:
```text
我喜歡吃巧克力 (I like eating chocolate)
```

Input 2:
```text
你也喜歡吃巧克力嗎？ (Do you also like eating chocolate?)
```

Expected Router (Input 2):
```json
{
  "mode": "conversation",
  "subject": "assistant"
}
```

Expected BM25:
```text
included=True
```

The conversation continues with chocolate context.

## Scenario 3: Single-Turn Relevance from Last Two Turns

Input 1: `我喜歡吃巧克力` (I like eating chocolate)  
Input 2: `東京現在幾點？` (What time is it in Tokyo?)  
Input 3: `巧克力有哪些種類？` (What kinds of chocolate are there?)  

Input 3 evaluation:
- `我喜歡吃巧克力` -> `included=True`
- `東京現在幾點？` -> `included=False`

Verifies that turns are scored individually rather than bulk-included.

## Scenario 4: Ambiguous Input Defaults to Conversation

Input:
```text
幫我查一下 (Look it up for me)
```

Expected Router:
```json
{
  "mode": "conversation",
  "subject": "unknown"
}
```

The assistant naturally asks what the user wants to search for without failing into a `clarify` error state.

## Scenario 5: File Search Plugin Tool

Input:
```text
幫我找履歷.pdf (Find resume.pdf for me)
```

Expected Router:
```json
{
  "mode": "tool",
  "subject": "履歷.pdf",
  "tool": "file_search",
  "fileName": "履歷.pdf"
}
```

Tool executes via `LazyDllToolLoader` and outputs search results to the console without sending file paths to TTS.
