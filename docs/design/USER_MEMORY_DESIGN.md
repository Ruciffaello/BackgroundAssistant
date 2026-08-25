# 對話資料與長期記憶設計邊界

## 實作狀態

截至 2026-08-24，已實作的只有固定使用者、完整對話回合、最近兩輪讀取及 BM25 相關性篩選。`MemoryItems` 尚未寫入或搜尋；Profile、MemoryWorker、敏感資料確認、忘記／清除及保存期限均尚未實作。下文標示為「未來規則」的內容不是現有功能。

## 目標

目前階段只處理兩件事：

1. 保存完整使用者／助理回合。
2. 回答前只帶入與目前輸入有詞彙關聯的最近對話。

本文件是使用者記憶功能的第一版實作基準。若其他草案與本文件衝突，以本文件為準。

## 整體流程

```text
STT 輸入 -> 語音轉文字與文字整理 --+
                                      +-> Router
CMD 輸入 -----------------------------+     |-- conversation -> BM25 最近兩輪 -> LLM 回答
                                            `-- tool -> McpToolExecutor
                                                        |
                                                        `-> 完成回合寫入 SQLite
```

- STT 與 CMD 在取得乾淨文字後共用同一條流程。
- 現有 Channel 與 GlobalState 已負責輸入排序，第一版不增加 `TurnId`。
- 目前沒有背景記憶分析或 `MemoryJob`。

## 第一版元件

目前已建立：

- `AgentMemoryDatabase`：migration、固定 `local-default`、完整回合寫入及最近回合讀取。
- `RecentConversationService`：取得最近兩輪並組合相關 Context。
- `Bm25RelevanceScorer`：對目前輸入與候選使用者原文評分。

目前沒有額外建立尚未使用的 Identity Resolver、Repository、MemoryWorker 或 Policy 抽象。

## SQLite 結構

第一階段使用獨立的 `agent_memory.db`，只建立四張表：

| 資料表 | 用途 |
| --- | --- |
| `SchemaMigrations` | 資料庫版本與 migration 紀錄 |
| `Users` | 使用者基本識別；目前只有 `local-default` |
| `ConversationMessages` | 已完成的使用者／助理回合 |
| `MemoryItems` | 後續明確記憶使用的純文字資料 |

`ConversationMessages` 會保存完成的回合。回答前最多讀取兩輪，以目前輸入和各輪 `UserText` 計算 BM25；中文字元使用 bigram，並排除「什麼、怎麼、如何、請問、知道」等通用問句詞。相同使用者輸入及具有明顯重複輸出的舊回合不作為候選。只有達 `MinimumBm25Score` 的回合才將使用者與助理文字一起加入 Prompt。`MemoryItems` 尚未接上保存或搜尋流程。Profile 與 Session 資料表不存在。

## 長期記憶判斷與安全規則（未來規則，尚未實作）

若未來加入長期記憶，必須另外確認抽取與安全規則。現行 Router 不輸出任何記憶標記，也沒有以下 `None`／`Likely`／`Explicit`／`Forbidden` 分類：

- `None`：看不出需要記憶。
- `Likely`：可能是穩定偏好或個人資料。
- `Explicit`：使用者明確要求記住。
- `Forbidden`：疑似禁止保存的資料。

以下 Policy 結果同樣只是待確認的設計方向，現行程式沒有 `MemoryWorker` 或 `MemoryPolicy`：

政策結果只有三種：

- `Allow`：可以直接儲存的一般偏好或穩定資料。
- `RequireConfirmation`：健康、財富等敏感資料，先取得使用者確認。
- `Reject`：密碼、身分證號、驗證碼、金鑰及其他高度敏感資料，不得儲存。

第一版同一時間只在記憶體保留一筆待確認變更，不建立 `PendingProfileChanges` 資料表。程式重啟後遺失該待確認項目可以接受。

### 記憶衝突與更新

第一版採用「明確才覆蓋、相容才合併、不確定就不存」：

- `Replace`：使用者明確更正舊資料或表達狀態已改變，更新既有資料，不保留版本歷史。
- `Merge`：新資料與舊資料相容，合併並去除重複項目。
- `Ignore`：新舊資料衝突或語意不明，但使用者沒有明確表示變更；保留舊資料，新內容只留在對話紀錄。
- 敏感資料即使符合 `Replace` 或 `Merge`，仍必須先經過 `RequireConfirmation`。
- 禁止保存的資料一律 `Reject`，不得進入更新判斷。

若衝突資料會影響當前回答，Agent 可以向使用者確認；否則不應為了補齊 Profile 主動打斷對話。

### 保存與刪除週期（提案，尚未實作）

- `UserProfiles` 與 `MemoryItems` 持續保存，直到使用者要求修改或刪除。
- `ConversationMessages` 保存最近 30 天。
- 訊息清除後，沒有任何訊息的 `ConversationSessions` 可以一併刪除。
- 程式啟動時執行一次過期資料清理，第一版不建立背景排程。
- 第一版不預設資料庫容量上限，先觀察實際增長速度。

使用者要求「忘記」某項資料時：

1. 刪除對應的結構化 Profile 欄位與 `MemoryItems`。
2. 同時刪除能明確定位的相關對話訊息。
3. 若無法可靠定位，讓使用者選擇清除目前 Session 或全部對話，不以模糊比對大量刪除訊息。

## Agent 個性（未來提案，尚未實作）

Agent 個性使用 `agent_profile.json`，與使用者資料分開：

- `core`：不可由 Agent 自行修改的核心限制。
- `personality`：可提出修改建議的個性設定。

修改流程為「Agent 提案 -> 使用者確認 -> schema 驗證 -> 原子寫入並保留備份」。第一版不允許 Agent 未經確認直接改寫自己，也不把每個使用者偏好寫回全域 Agent 個性。

## 第一版刻意不做

以下項目延後，沒有實際需求前不建立抽象層或資料表：

- `TurnId`
- 聲紋資料表與聲紋辨識
- `UserTraits`、`UserInterests`、`HealthRecords`、`Relationships` 等細分資料表
- 每種資料各自一個 Repository
- 多個細分 Policy 類別
- `PendingProfileChanges` 持久化
- 完整 Profile 修改歷史與 `IsActive` 版本鏈
- 固定每八輪摘要；改為 token 壓力出現時才摘要
- Embedding、向量索引與語意 RAG；目前只有最近對話的 BM25 詞彙篩選
- 讓使用者查詢 Profile 的 Tool
- DLL 擴充工具
- 多使用者同時寫入與分散式併發控制
- 資料庫加密與商業授權

## 未來擴充邊界

- 聲紋辨識只需新增另一個 `IUserIdentityResolver` 實作，不改動後續流程。
- 長期記憶搜尋若實作，應作為 `conversation` 回答前的 Context Provider，不恢復獨立 `retrieve` action。
- Profile JSON 需要複雜查詢時，再透過 migration 拆表。
- 待確認資料需要跨重啟保存時，再新增持久化佇列。
- DLL 熱抽換依獨立設計文件實作，不與第一版記憶功能綁定。

## 目前階段完成條件

- CMD 與 STT 共用 Router、回答、工具及對話寫入流程。
- 完成回合可在重啟後讀回。
- 最近最多兩輪逐筆經 BM25 過濾，無關內容不加入 Prompt。
- 相關 Context 加入後仍不超過模型 token 預算。
- 長期記憶、Profile 與安全政策不被誤稱為已完成。

完整驗收案例見[使用者記憶第一版驗收清單](../testing/USER_MEMORY_VERIFICATION.md)。
