# Parser 重新設計

## 狀態

第一階段已實作。現有 `IntentParserWorker` 已從主題分類器改為決策路由器。

## 決策流程

```text
Input
  -> Decision Router
     |-- answer   -> LLM 直接回答
     |-- chat     -> 對話回應（個性與使用者記憶待接入）
     |-- support  -> 情緒支持（專門 Safety Evaluator 待接入）
     |-- tool     -> Tool Planner -> McpToolExecutor
     |-- clarify  -> 向使用者追問
     `-- retrieve -> Memory / RAG（尚未實作）
```

Router 只接受以下 JSON：

```json
{"action":"answer"}
```

```json
{"action":"chat"}
```

```json
{"action":"support"}
```

```json
{"action":"tool"}
```

```json
{"action":"clarify","question":"你想查詢什麼內容？"}
```

```json
{"action":"retrieve","source":"memory","query":"使用者所指的專案"}
```

`retrieve.source` 目前只允許 `memory` 或 `rag`；其他值會轉為 `clarify`。在 Retrieval Provider 完成前，合法的 `retrieve` 會回覆資料尚未接入，不會形成循環。

## Token 預算

模型 Context 上限由 `OnnxSettings:Phi35:MaxContextLimit` 控制，目前為 1024 tokens。

| 階段 | 預留輸出 |
| --- | ---: |
| Decision Router | 48 |
| Tool Planner | 96 |
| Direct Answer | 300 |
| Safety Margin | 16 |

每次推論前使用模型實際 Tokenizer 計算 Prompt。輸入過長時會逐步縮短使用者文字，確保保留輸出與安全空間；Prompt 模板本身超過預算時拒絕推論。

## 已移除的舊行為

- `News / Pokemon / Time / Knowledge / Humor / None` 主題分類。
- 依分類選擇 Extractor。
- 2～5 字強制視為人名。
- SQLite 熱詞優先繞過 LLM Router。
- 使用 `[CLEAN]...[END]` Regex 解析分類。

工具 Planner 目前仍輸出既有 Executor 可接受的扁平 JSON，例如：

```json
{"tool":"get_time","location":"Tokyo"}
```

## 後續工作

- 建立 Memory 與 RAG Provider。
- 建立 Request Context 與有上限的重新決策循環。
- 將工具描述與 JSON Schema 從 Prompt 移到可動態取得的 Tool Registry。
- 為 Router、Planner 與 Token Budget 增加自動化測試。
- 將 `IntentParserWorker` 更名為 `DecisionRouterWorker` 或改由 `RequestOrchestrator` 管理。
