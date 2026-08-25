using Microsoft.Data.Sqlite;

namespace BackgroundAssistant.Memory;

/// <summary>
/// 單一對話回合資料記錄（包含使用者發言與助理回覆）。
/// </summary>
/// <param name="UserText">使用者發言內容。</param>
/// <param name="AssistantText">助理回覆內容。</param>
public sealed record ConversationTurn(string UserText, string AssistantText);

/// <summary>
/// 管理對話歷史與記憶的 SQLite 資料庫存取層。
/// 負責建立 Schema Migration、使用者資料表、歷史回合記錄以及 MemoryItems 資料表結構。
/// </summary>
public sealed class AgentMemoryDatabase
{
    /// <summary>
    /// 預設本機使用者識別碼。
    /// </summary>
    public const string LocalUserId = "local-default";

    private readonly string _connectionString;
    private readonly ILogger<AgentMemoryDatabase> _logger;

    private static readonly (int Version, string Name, string Sql)[] Migrations =
    [
        (1, "initial_conversation_and_memory_schema", """
            CREATE TABLE Users (
                UserId TEXT PRIMARY KEY,
                CreatedUtc TEXT NOT NULL
            );

            CREATE TABLE ConversationMessages (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
                UserText TEXT NOT NULL,
                AssistantText TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL
            );

            CREATE INDEX IX_ConversationMessages_UserId_Id
                ON ConversationMessages(UserId, Id DESC);

            CREATE TABLE MemoryItems (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                UserId TEXT NOT NULL REFERENCES Users(UserId) ON DELETE CASCADE,
                Content TEXT NOT NULL,
                NormalizedContent TEXT NOT NULL,
                CreatedUtc TEXT NOT NULL,
                UpdatedUtc TEXT NOT NULL,
                UNIQUE(UserId, NormalizedContent)
            );
            """)
    ];

    /// <summary>
    /// 初始化 <see cref="AgentMemoryDatabase"/> 的新執行個體，並自動執行資料庫初始化與 Migration。
    /// </summary>
    /// <param name="configuration">應用程式組態設定。</param>
    /// <param name="logger">記錄器實例。</param>
    public AgentMemoryDatabase(IConfiguration configuration, ILogger<AgentMemoryDatabase> logger)
    {
        _logger = logger;
        string databasePath = configuration["ConversationDatabase:Path"] ?? "agent_memory.db";
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

    /// <summary>
    /// 取得指定使用者最近的 N 筆對話回合（按時間順序由舊至新排列）。
    /// </summary>
    /// <param name="userId">使用者識別碼。</param>
    /// <param name="count">欲讀取的回合數量上限。</param>
    /// <returns>對話回合清單。</returns>
    public IReadOnlyList<ConversationTurn> GetRecentTurns(string userId, int count)
    {
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT UserText, AssistantText
            FROM ConversationMessages
            WHERE UserId = $userId
            ORDER BY Id DESC
            LIMIT $count;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$count", count);

        using SqliteDataReader reader = command.ExecuteReader();
        var turns = new List<ConversationTurn>();
        while (reader.Read())
        {
            turns.Add(new ConversationTurn(reader.GetString(0), reader.GetString(1)));
        }
        turns.Reverse();
        return turns;
    }

    /// <summary>
    /// 將單一對話回合新增寫入 SQLite 資料庫中。
    /// </summary>
    /// <param name="userId">使用者識別碼。</param>
    /// <param name="userText">使用者發言文字。</param>
    /// <param name="assistantText">助理回覆文字。</param>
    public void AddTurn(string userId, string userText, string assistantText)
    {
        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(assistantText)) return;

        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ConversationMessages(UserId, UserText, AssistantText, CreatedUtc)
            VALUES ($userId, $userText, $assistantText, $createdUtc);
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$userText", userText.Trim());
        command.Parameters.AddWithValue("$assistantText", assistantText.Trim());
        command.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 初始化資料庫，包含建立版本遷移表記錄、依序套用 Migration 並插入預設使用者。
    /// </summary>
    private void Initialize()
    {
        using SqliteConnection connection = OpenConnection();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS SchemaMigrations (
                    Version INTEGER PRIMARY KEY,
                    Name TEXT NOT NULL,
                    AppliedUtc TEXT NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var appliedVersions = new HashSet<int>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT Version FROM SchemaMigrations;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read()) appliedVersions.Add(reader.GetInt32(0));
        }

        foreach ((int version, string name, string sql) in Migrations)
        {
            if (appliedVersions.Contains(version)) continue;

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand migration = connection.CreateCommand();
            migration.Transaction = transaction;
            migration.CommandText = sql;
            migration.ExecuteNonQuery();

            using SqliteCommand record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = """
                INSERT INTO SchemaMigrations(Version, Name, AppliedUtc)
                VALUES ($version, $name, $appliedUtc);
                """;
            record.Parameters.AddWithValue("$version", version);
            record.Parameters.AddWithValue("$name", name);
            record.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.ToString("O"));
            record.ExecuteNonQuery();
            transaction.Commit();
            _logger.LogInformation("Applied conversation database migration {version}: {name}.", version, name);
        }

        using SqliteCommand user = connection.CreateCommand();
        user.CommandText = """
            INSERT OR IGNORE INTO Users(UserId, CreatedUtc)
            VALUES ($userId, $createdUtc);
            """;
        user.Parameters.AddWithValue("$userId", LocalUserId);
        user.Parameters.AddWithValue("$createdUtc", DateTimeOffset.UtcNow.ToString("O"));
        user.ExecuteNonQuery();
    }

    /// <summary>
    /// 開啟 SQLite 連線並啟用外鍵約束與 Busy 逾時設定。
    /// </summary>
    /// <returns>已開啟的 <see cref="SqliteConnection"/>。</returns>
    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 3000;";
        command.ExecuteNonQuery();
        return connection;
    }
}
