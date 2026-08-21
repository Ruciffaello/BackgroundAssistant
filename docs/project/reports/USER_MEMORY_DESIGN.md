# 使用者記憶設計（精簡版 V1）

## 目標

第一版只處理三件事：

1. 保留最近對話，讓回答能延續上下文。
2. 儲存經過安全規則檢查的使用者資料與長期記憶。
3. 為未來的聲紋辨識、RAG 與個性調整保留介面，但現在不實作。

本文件是使用者記憶功能的第一版實作基準。若其他草案與本文件衝突，以本文件為準。

## 整體流程

```text
STT 輸入 -> 語音轉文字與文字整理 --+
                                      +-> UserId -> Router -> 回答或工具 -> 立即輸出
CMD 輸入 -----------------------------+                         |
                                                                `-> MemoryJob
                                                                     |
                                                                     `-> 背景 MemoryWorker
                                                                          -> MemoryPolicy
                                                                          -> SQLite
```

- STT 與 CMD 在取得乾淨文字後共用同一條流程。
- 現有 Channel 與 GlobalState 已負責輸入排序，第一版不增加 `TurnId`。
- 回答完成後立即輸出；記憶分析在背景執行，不阻塞使用者。
- `MemoryJob` 必須攜帶完整快照，背景工作不回頭尋找某一輪對話。

```csharp
public sealed record MemoryJob(
    string UserId,
    string Input,
    string Route,
    string Response,
    MemoryReviewHint ReviewHint);
```

## 第一版元件

只建立以下元件：

- `DefaultUserIdentityResolver`：目前固定回傳 `local-default`。
- `AgentMemoryStore`：唯一的 SQLite 存取入口。
- `ConversationContextService`：提供最近兩輪對話；接近 token 上限時才摘要。
- `MemoryWorker`：在背景分析並提出記憶候選項目。
- `MemoryPolicy`：決定允許儲存、需要確認或拒絕。
- `LogRedactor`：避免敏感資料進入 log。
- `AgentProfileService`：讀取與安全更新 Agent 個性 JSON。

介面只保留確定會替換的邊界：

```csharp
public interface IUserIdentityResolver
{
    ValueTask<string> ResolveUserIdAsync(CancellationToken cancellationToken);
}

public interface IAgentMemoryStore
{
    // 實際方法在實作階段依使用案例加入，不預先拆成多個 Repository。
}
```

## SQLite 結構

第一版使用獨立的 `agent_memory.db`，只建立六張表：

| 資料表 | 用途 |
| --- | --- |
| `SchemaMigrations` | 資料庫版本與 migration 紀錄 |
| `Users` | 使用者基本識別；目前只有 `local-default` |
| `UserProfiles` | 一位使用者一份 `ProfileJson` |
| `ConversationSessions` | 對話工作階段 |
| `ConversationMessages` | 對話訊息 |
| `MemoryItems` | 可檢索的長期事實或偏好 |

`UserProfiles.ProfileJson` 可包含：

- 個性特徵
- 興趣
- 年齡
- 職業
- 財富狀況（預設「一般」）
- 健康狀況
- 家人名單
- 朋友名單

第一版不將每一類資料拆成獨立資料表。當查詢、索引或關聯需求實際出現時，再以 migration 正規化。

## 記憶判斷與安全規則

Router 前只做便宜的初步標記：

- `None`：看不出需要記憶。
- `Likely`：可能是穩定偏好或個人資料。
- `Explicit`：使用者明確要求記住。
- `Forbidden`：疑似禁止保存的資料。

回答後，`MemoryWorker` 使用當輪快照抽取候選資料；最終是否寫入由 C# `MemoryPolicy` 決定，不能讓 LLM 自行決定。

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

### 保存與刪除週期

- `UserProfiles` 與 `MemoryItems` 持續保存，直到使用者要求修改或刪除。
- `ConversationMessages` 保存最近 30 天。
- 訊息清除後，沒有任何訊息的 `ConversationSessions` 可以一併刪除。
- 程式啟動時執行一次過期資料清理，第一版不建立背景排程。
- 第一版不預設資料庫容量上限，先觀察實際增長速度。

使用者要求「忘記」某項資料時：

1. 刪除對應的結構化 Profile 欄位與 `MemoryItems`。
2. 同時刪除能明確定位的相關對話訊息。
3. 若無法可靠定位，讓使用者選擇清除目前 Session 或全部對話，不以模糊比對大量刪除訊息。

## Agent 個性

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
- Embedding、向量索引與 RAG
- 讓使用者查詢 Profile 的 Tool
- DLL 擴充工具
- 多使用者同時寫入與分散式併發控制
- 資料庫加密與商業授權

## 未來擴充邊界

- 聲紋辨識只需新增另一個 `IUserIdentityResolver` 實作，不改動後續流程。
- RAG 由 Router 的 `retrieve` 路徑接入；檢索結果仍需重新判斷是否足以回答。
- Profile JSON 需要複雜查詢時，再透過 migration 拆表。
- 待確認資料需要跨重啟保存時，再新增持久化佇列。
- DLL 熱抽換依獨立設計文件實作，不與第一版記憶功能綁定。

## 第一版完成條件

- CMD 與 STT 都能取得 `local-default` 並共用後續流程。
- 回答可帶入最近兩輪對話，且不超過模型 token 預算。
- 回答輸出不等待記憶分析完成。
- 一般偏好可寫入，敏感資料會要求確認，禁止資料不會寫入或記錄至 log。
- 重啟後可以讀回已確認的 Profile 與長期記憶。
- 尚未實作的延後項目不產生空介面、空資料表或預留流程。

完整驗收案例見[使用者記憶第一版驗收清單](USER_MEMORY_VERIFICATION.md)。
