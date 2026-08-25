# Parser 重新設計

## 狀態

第二版已實作至建置通過，實機案例仍在驗證。`IntentParserWorker` 現在只區分一般對話與明確工具需求。

## 決策流程

```text
Input
  -> Decision Router
     |-- conversation -> BM25 篩選最近兩輪 -> 對話 LLM
     `-- tool         -> McpToolExecutor
```

Router 一次輸出模式、主詞，以及工具模式需要的完整命令。一般對話：

```json
{"mode":"conversation","subject":"assistant"}
```

時間工具：

```json
{"mode":"tool","subject":"東京時間","tool":"get_time","location":"Tokyo"}
```

新聞、卡牌與關機工具同樣使用扁平 JSON，直接交給 `McpToolExecutor`。Router 不再輸出 `answer`、`chat`、`support`、`retrieve` 或 `clarify`，也不再呼叫第二次 Tool Planner。無效 JSON、未知模式或不可用工具預設回到 `conversation`。

## Token 預算

模型 Context 上限由 `OnnxSettings:Phi35:MaxContextLimit` 控制，目前為 1024 tokens。

| 階段 | 預留輸出 |
| --- | ---: |
| Decision Router | 96 |
| Conversation Answer | 300 |
| Safety Margin | 16 |

每次推論前使用模型實際 Tokenizer 計算 Prompt。現有超長處理仍是逐步截短整體輸入，不是真正摘要；後續應先移除低相關 Context，再處理目前輸入。

## 已移除的舊行為

- `News / Pokemon / Time / Knowledge / Humor / None` 主題分類。
- 依分類選擇 Extractor。
- 2～5 字強制視為人名。
- SQLite 熱詞優先繞過 LLM Router。
- 使用 `[CLEAN]...[END]` Regex 解析分類。

Router 現在直接輸出 Executor 可接受的扁平 JSON，例如：

```json
{"tool":"get_time","location":"Tokyo"}
```

## 後續工作

- 依 BM25 實機分數調整門檻、分詞與可能的停用詞。
- 超過 token 上限時先移除低相關 Context；目前輸入本身過長時才設計濃縮策略。
- 將工具描述與 JSON Schema 從 Prompt 移到可動態取得的 Tool Registry。
- 為 Router、BM25 與 Token Budget 增加自動化測試。
- 將 `IntentParserWorker` 更名為 `DecisionRouterWorker` 或改由 `RequestOrchestrator` 管理。
