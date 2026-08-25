# Parser Redesign

## Status

Version 2 is fully implemented and passes all builds; real-device scenario validation is ongoing. `IntentParserWorker` now only distinguishes between general conversation and explicit tool requests.

## Decision Flow

```text
Input
  -> Decision Router
     |-- conversation -> BM25 filtering (last 2 turns) -> Conversation LLM
     `-- tool         -> McpToolExecutor
```

The Router outputs mode, subject, and the complete command required for tool mode in a single inference step. General conversation:

```json
{"mode":"conversation","subject":"assistant"}
```

Time tool:

```json
{"mode":"tool","subject":"東京時間","tool":"get_time","location":"Tokyo"}
```

News, cards, and shutdown tools also use flat JSON structures, passed directly to `McpToolExecutor`. The Router no longer outputs `answer`, `chat`, `support`, `retrieve`, or `clarify`, nor does it invoke a second Tool Planner pass. Invalid JSON, unknown modes, or unavailable tools safely fall back to `conversation`.

## Token Budget

The model context limit is controlled by `OnnxSettings:Phi35:MaxContextLimit`, currently set to 1024 tokens.

| Phase | Reserved Output |
| --- | ---: |
| Decision Router | 96 |
| Conversation Answer | 300 |
| Safety Margin | 16 |

Prompt length is calculated before each inference using the model's actual Tokenizer. The existing overflow handling gradually truncates the entire input rather than creating a true summary; future improvements should drop low-relevance context first before processing the current input.

## Removed Legacy Behaviors

- `News / Pokemon / Time / Knowledge / Humor / None` topic classifications.
- Classification-based Extractor selection.
- 2–5 character inputs forced as person names.
- SQLite hotwords bypassing the LLM Router.
- `[CLEAN]...[END]` regex classification parsing.

The Router now directly produces flat JSON accepted by the Executor, for example:

```json
{"tool":"get_time","location":"Tokyo"}
```

## Next Steps

- Fine-tune thresholds, tokenization, and potential stopwords based on real BM25 scores.
- When exceeding token budget, discard low-relevance context first; compress current input only when input itself is oversized.
- Move tool descriptions and JSON schemas from prompts into a dynamic Tool Registry.
- Add automated tests for Router, BM25, and Token Budget.
- Rename `IntentParserWorker` to `DecisionRouterWorker` or manage via a `RequestOrchestrator`.
