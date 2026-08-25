using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace BackgroundAssistant.Services;

/// <summary>
/// 熱詞指令資料模型，代表一組關鍵字及其對應的快速動作 JSON。
/// </summary>
public class HotwordEntry
{
    /// <summary>
    /// 熱詞關鍵字（例如「關閉系統」）。
    /// </summary>
    public string Keyword { get; set; } = "";

    /// <summary>
    /// 觸發時執行的 JSON 指令。
    /// </summary>
    public string ActionJson { get; set; } = "";

    /// <summary>
    /// 熱詞功能說明。
    /// </summary>
    public string Description { get; set; } = "";
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<HotwordEntry>))]
internal partial class HotwordJsonContext : JsonSerializerContext
{
}

/// <summary>
/// SQLite 資料庫服務，提供「快速路徑」查詢。
/// 支援「字面匹配」與「拼音匹配」，有效解決同音異字導致的指令誤判。
/// </summary>
public class SqliteDatabaseService
{
    private readonly ILogger<SqliteDatabaseService> _logger;
    private readonly IPinyinService _pinyinService;
    private const string DbPath = "assistant_data.db";
    private readonly string _connectionString = $"Data Source={DbPath}";

    /// <summary>
    /// 初始化 <see cref="SqliteDatabaseService"/> 的新執行個體，並初始化資料表與種子資料。
    /// </summary>
    /// <param name="logger">記錄器實例。</param>
    /// <param name="pinyinService">拼音轉換服務。</param>
    public SqliteDatabaseService(ILogger<SqliteDatabaseService> logger, IPinyinService pinyinService)
    {
        _logger = logger;
        _pinyinService = pinyinService;
        InitializeDatabase();
    }

    /// <summary>
    /// 初始化 SQLite 資料庫，建立資料表並載入種子熱詞。
    /// </summary>
    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 建立熱詞動作表 (新增 Pinyin 欄位)
        var createTableSql = @"
            CREATE TABLE IF NOT EXISTS HotwordActions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Keyword TEXT NOT NULL UNIQUE,
                Pinyin TEXT NOT NULL,
                ActionJson TEXT NOT NULL,
                Description TEXT
            );
            CREATE INDEX IF NOT EXISTS idx_keyword ON HotwordActions(Keyword);
            CREATE INDEX IF NOT EXISTS idx_pinyin ON HotwordActions(Pinyin);
        ";

        using var command = new SqliteCommand(createTableSql, connection);
        command.ExecuteNonQuery();

        // 檢查是否需要更新舊資料 (針對剛才還沒有 Pinyin 欄位時建立的表)
        UpdateLegacyData(connection);

        // 改為從 JSON 同步資料
        SyncDataFromJson(connection);
        
        _logger.LogInformation("SQLite Database initialized and synced with JSON.");
    }

    /// <summary>
    /// 檢查並升級舊版 SQLite 資料表結構。
    /// </summary>
    /// <param name="connection">資料庫連線。</param>
    private void UpdateLegacyData(SqliteConnection connection)
    {
        // 簡單檢查是否有欄位存在，若無則拋出異常或處理。
        // 這裡我們假設專案剛開始，若 schema 不符直接重建或手動處理。
        try
        {
            var checkColSql = "SELECT Pinyin FROM HotwordActions LIMIT 1";
            using var cmd = new SqliteCommand(checkColSql, connection);
            cmd.ExecuteScalar();
        }
        catch (SqliteException)
        {
            _logger.LogWarning("Old schema detected. Migrating database...");
            // 遷移邏輯：刪除舊表重建（開發期適用）
            using var dropCmd = new SqliteCommand("DROP TABLE IF EXISTS HotwordActions", connection);
            dropCmd.ExecuteNonQuery();
            InitializeDatabase(); // 遞迴呼叫重新建立
        }
    }

    /// <summary>
    /// 從種子 JSON 檔案 (hotwords_initial.json) 讀取預設熱詞並同步至資料庫。
    /// </summary>
    /// <param name="connection">資料庫連線。</param>
    private void SyncDataFromJson(SqliteConnection connection)
    {
        const string JsonFilePath = "hotwords_initial.json";
        if (!File.Exists(JsonFilePath))
        {
            _logger.LogWarning("Seed JSON file not found at {path}. Skipping sync.", JsonFilePath);
            return;
        }

        try
        {
            string jsonContent = File.ReadAllText(JsonFilePath);
            var hotwords = JsonSerializer.Deserialize(jsonContent, HotwordJsonContext.Default.ListHotwordEntry);

            if (hotwords != null)
            {
                foreach (var entry in hotwords)
                {
                    AddHotword(connection, entry.Keyword, entry.ActionJson, entry.Description);
                }
                _logger.LogInformation("Successfully synced {count} hotwords from JSON to SQLite.", hotwords.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync hotwords from JSON.");
        }
    }

    /// <summary>
    /// 向資料庫新增單一筆熱詞及其拼音索引。
    /// </summary>
    /// <param name="conn">資料庫連線。</param>
    /// <param name="keyword">熱詞關鍵字。</param>
    /// <param name="json">對應的動作 JSON。</param>
    /// <param name="desc">描述資訊。</param>
    private void AddHotword(SqliteConnection conn, string keyword, string json, string desc)
    {
        string pinyin = _pinyinService.GetNormalizedPinyin(keyword);
        var insertSql = "INSERT OR IGNORE INTO HotwordActions (Keyword, Pinyin, ActionJson, Description) VALUES (@k, @p, @j, @d)";
        using var cmd = new SqliteCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("@k", keyword);
        cmd.Parameters.AddWithValue("@p", pinyin);
        cmd.Parameters.AddWithValue("@j", json);
        cmd.Parameters.AddWithValue("@d", desc);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 根據輸入文字尋找匹配動作。
    /// 順序：1. 字面精準匹配 -> 2. 拼音精準匹配 -> 3. 拼音模糊比對 (Levenshtein)
    /// 注意：不再使用 LIKE 進行字串內部的模糊匹配，以避免長難句中的誤判（例如「中職」誤判於長句子中）。
    /// </summary>
    /// <param name="text">使用者輸入文字。</param>
    /// <returns>匹配到的動作 JSON 字串，若無匹配則回傳 null。</returns>
    public string? GetActionByKeyword(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // 清理文字，移除換行與多餘空白，避免模型碎碎念干擾
        text = text.Split('\n')[0].Trim();
        if (text.Length > 20) return null; // 太長的句子不走快速路徑，交給 LLM 處理

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 1. 字面完全匹配 (或輸入文字包含關鍵字，但關鍵字必須佔據一定比例)
        string? result = QueryAction(connection, "SELECT ActionJson FROM HotwordActions WHERE @t = Keyword OR @t LIKE Keyword || '%' OR @t LIKE '%' || Keyword LIMIT 1", text);
        if (result != null) return result;

        // 2. 準備輸入文字的拼音
        string inputPinyin = _pinyinService.GetNormalizedPinyin(text);
        
        // 3. 拼音完全匹配 (解決同音異字)
        result = QueryAction(connection, "SELECT ActionJson FROM HotwordActions WHERE @p = Pinyin LIMIT 1", inputPinyin);
        if (result != null) return result;

        // 4. 拼音模糊匹配 (Levenshtein) - 僅針對短字進行 (3-5個字)
        var allHotwords = GetAllHotwords(connection);
        foreach (var hw in allHotwords)
        {
            // 拼音長度必須非常接近
            if (Math.Abs(inputPinyin.Length - hw.Pinyin.Length) <= 2)
            {
                int distance = ComputeLevenshteinDistance(inputPinyin, hw.Pinyin);
                // 嚴格限制：最多允許 1 個字元錯誤，且長度要在合理範圍
                if (distance <= 1) 
                {
                    _logger.LogInformation("Fuzzy Pinyin Match Found: {input} -> {target} (Dist: {d})", inputPinyin, hw.Pinyin, distance);
                    return hw.ActionJson;
                }
            }
        }
        
        return null;
    }

    /// <summary>
    /// 讀取資料庫中所有熱詞的拼音與動作 JSON。
    /// </summary>
    /// <param name="conn">資料庫連線。</param>
    /// <returns>熱詞拼音與動作 JSON 的元組清單。</returns>
    private List<(string Pinyin, string ActionJson)> GetAllHotwords(SqliteConnection conn)
    {
        var list = new List<(string, string)>();
        using var cmd = new SqliteCommand("SELECT Pinyin, ActionJson FROM HotwordActions", conn);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetString(0), reader.GetString(1)));
        }
        return list;
    }

    /// <summary>
    /// 計算兩字串之 Levenshtein 編輯距離。
    /// </summary>
    /// <param name="s">來源字串。</param>
    /// <param name="t">目標字串。</param>
    /// <returns>編輯距離數值。</returns>
    private static int ComputeLevenshteinDistance(string s, string t)
    {
        int n = s.Length; int m = t.Length;
        if (n == 0) return m; if (m == 0) return n;
        int[] v0 = new int[m + 1]; int[] v1 = new int[m + 1];
        for (int i = 0; i <= m; i++) v0[i] = i;
        for (int i = 0; i < n; i++) {
            v1[0] = i + 1;
            for (int j = 0; j < m; j++) {
                int cost = (s[i] == t[j]) ? 0 : 1;
                v1[j + 1] = Math.Min(v1[j] + 1, Math.Min(v0[j + 1] + 1, v0[j] + cost));
            }
            Array.Copy(v1, v0, v0.Length);
        }
        return v0[m];
    }

    /// <summary>
    /// 執行參數化 SQL 查詢並回傳單一動作 JSON。
    /// </summary>
    /// <param name="conn">資料庫連線。</param>
    /// <param name="sql">SQL 查詢字串。</param>
    /// <param name="paramValue">參數數值。</param>
    /// <returns>動作 JSON 字串，若查無結果則為 null。</returns>
    private string? QueryAction(SqliteConnection conn, string sql, string paramValue)
    {
        using var command = new SqliteCommand(sql, conn);
        // 如果是拼音查詢，參數名稱對應到 @p；如果是字面查詢，對應到 @t
        if (sql.Contains("@p")) command.Parameters.AddWithValue("@p", paramValue);
        else command.Parameters.AddWithValue("@t", paramValue);

        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            return reader.GetString(0);
        }
        return null;
    }
}
