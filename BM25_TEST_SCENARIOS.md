# Conversation Router 與 BM25 測試情境

本文件用來手動驗證：

- `conversation`／`tool` 路由
- `subject` 判斷
- BM25 是否排除無關的最近對話
- BM25 是否保留相關的最近對話
- 一般對話是否保留完整的使用者輸入
- BM25 詞彙比對的已知限制

## 測試前注意事項

程式重啟不會清空 SQLite 對話紀錄，BM25 會讀取最近兩輪。因此不同測試可能互相影響，測試時應查看 `[2. BM25 Context]` 列出的候選文字，確認目前實際比較的是哪兩輪。

BM25 設定位於 `appsettings.json`：

```json
"ConversationRelevance": {
  "MaxTurns": 2,
  "MinimumBm25Score": 0.25
}
```

每筆候選會顯示：

```text
[2. BM25 Context]: score=0.000, included=False, text=候選使用者輸入
```

分數會受到最近兩輪的內容與長度影響，因此本文只要求 `included` 結果，不預設精確分數。

## 情境一：排除東京時間污染

依序輸入：

```text
現在東京幾點？
```

預期 Router：

```json
{
  "mode": "tool",
  "subject": "東京時間",
  "tool": "get_time",
  "location": "Tokyo"
}
```

接著輸入：

```text
你喜歡吃巧克力嗎？
```

預期 Router：

```json
{
  "mode": "conversation",
  "subject": "assistant"
}
```

預期 BM25：

```text
[2. BM25 Context]: score=0.000, included=False, text=現在東京幾點？
```

最後回答不應出現「東京」、「日本」、「時間」或「幾點」。

## 情境二：保留明確相關的對話

依序輸入：

```text
我喜歡吃巧克力
```

```text
你也喜歡吃巧克力嗎？
```

預期第二句 Router：

```json
{
  "mode": "conversation",
  "subject": "assistant"
}
```

預期 BM25：

```text
included=True
```

兩句共同包含「喜歡」、「歡吃」、「吃巧」、「巧克」及「克力」等 bigram。回答應圍繞巧克力，不應轉到其他話題。

## 情境三：相關主題的延伸問題

依序輸入：

```text
我最近正在學習C#
```

```text
C#適合開發什麼應用程式？
```

預期：

```text
mode=conversation
BM25 included=True
```

回答可以參考使用者正在學習 C# 的上下文，但仍應以目前問題為主。

## 情境四：切換到完全不同的主題

依序輸入：

```text
我最近正在學習C#
```

```text
你喜歡喝咖啡嗎？
```

預期：

```text
mode=conversation
subject=assistant
```

C# 對話應為：

```text
included=False
```

回答不應提到 C#、程式設計或開發。

## 情境五：最近兩輪只有一輪相關

依序輸入：

```text
我喜歡吃巧克力
```

```text
東京現在幾點？
```

```text
巧克力有哪些種類？
```

第三句應比較兩筆候選：

```text
我喜歡吃巧克力
→ included=True

東京現在幾點
→ included=False
```

這組用來驗證系統會逐筆評分，而不是將最近兩輪全部加入或全部排除。

## 情境六：模糊查詢回到一般對話

輸入：

```text
幫我查一下
```

因為沒有查詢對象，不應產生虛構的工具參數。預期：

```json
{
  "mode": "conversation",
  "subject": "unknown"
}
```

對話模型應自然追問使用者想查詢什麼，不應再出現 `clarify` action。

## 情境七：一般知識不應呼叫工具

分別輸入：

```text
什麼是黑洞？
```

```text
時間是什麼？
```

```text
為什麼巧克力會融化？
```

全部預期為：

```json
{
  "mode": "conversation",
  "subject": "對應主題"
}
```

「時間是什麼？」是在詢問時間的概念，不是目前幾點，因此不應呼叫 `get_time`。

## 情境八：本地時間工具

輸入：

```text
現在幾點？
```

預期：

```json
{
  "mode": "tool",
  "subject": "目前時間",
  "tool": "get_time",
  "location": "Local"
}
```

接著應看到 `[3. Tool Command]` 與 `[4. Execution Result]`，回答應包含本地時間。

## 情境九：不同地區時間

分別輸入：

```text
東京現在幾點？
紐約現在幾點？
倫敦現在幾點？
```

預期工具與參數分別為：

```json
{"tool":"get_time","location":"Tokyo"}
```

```json
{"tool":"get_time","location":"New York"}
```

```json
{"tool":"get_time","location":"London"}
```

Router 可以帶有額外的 `mode` 與 `subject`；`TimeTools` 會忽略不需要的欄位。

## 情境十：新聞工具

輸入：

```text
查一下台積電的最新新聞
```

預期：

```json
{
  "mode": "tool",
  "subject": "台積電",
  "tool": "rss_news_search",
  "query": "台積電"
}
```

不應只輸出 `{"mode":"tool"}`，因為 Router 必須在同一次分析中產生完整工具參數。

## 情境十一：寶可夢卡牌工具

輸入：

```text
幫我找噴火龍卡牌
```

預期：

```json
{
  "mode": "tool",
  "subject": "噴火龍",
  "tool": "ptcg_search",
  "query": "噴火龍"
}
```

應進入卡牌工具，不應被當成一般知識問題。

## 情境十二：主詞判斷

分別測試：

```text
我喜歡巧克力
```

```json
{"mode":"conversation","subject":"user"}
```

```text
你喜歡巧克力嗎？
```

```json
{"mode":"conversation","subject":"assistant"}
```

```text
小明喜歡巧克力嗎？
```

```json
{"mode":"conversation","subject":"小明"}
```

```text
巧克力為什麼是甜的？
```

```json
{"mode":"conversation","subject":"巧克力"}
```

這組只驗證分析輸出。目前 `subject` 尚未參與回答生成或 BM25 計算。

## 情境十三：BM25 的語意限制

依序輸入：

```text
我最愛吃巧克力
```

```text
你也喜歡嗎？
```

兩句語意相關，但「最愛」與「喜歡」不是相同詞彙，因此 BM25 可能得到低分並排除上一輪。

這不是 BM25 計算錯誤，而是它只比較詞彙、不理解完整語意。這組應保留為評估 BM25 是否足夠的反例。

## 情境十四：通用詞造成誤命中

依序輸入：

```text
你知道東京現在幾點嗎？
```

```text
你知道巧克力怎麼製作嗎？
```

兩句共同包含「你知道」。目前分詞器已將「知道」列為停用詞，預期不應只因這個通用詞而達到門檻。

若仍因其他通用詞超過門檻而加入，後續可能需要評估：

- 中文停用詞，例如「你」、「我」、「知道」及「請問」
- 調整最低分數
- 要求至少命中一定數量的有效 bigram

先記錄實際分數，不要只依這一個案例立即修改演算法。

## 測試記錄表

| 測試 | Router mode | subject | tool | BM25 分數 | 是否加入 | 回答是否污染 | 備註 |
|---|---|---|---|---:|---|---|---|
| 東京 → 巧克力 |  |  |  |  |  |  |  |
| 巧克力 → 巧克力 |  |  |  |  |  |  |  |
| C# → 咖啡 |  |  |  |  |  |  |  |
| 兩輪僅一輪相關 |  |  |  |  |  |  |  |
| 最愛 → 喜歡 |  |  |  |  |  |  |  |
| 共同通用詞 |  |  |  |  |  |  |  |

## 建議優先順序

優先執行情境一、二、五、十三及十四。這五組能快速確認 BM25 是否解決東京時間污染，同時觀察詞彙比對可能造成的漏判與誤判。
