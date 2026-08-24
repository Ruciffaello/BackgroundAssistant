using Microsoft.Data.Sqlite;

namespace BackgroundAssistant.Memory;

public sealed record ConversationTurn(string UserText, string AssistantText);

/// <summary>
/// Owns the small SQLite database used by conversation context and future explicit memories.
/// No memory extraction or retrieval policy belongs in this class.
/// </summary>
public sealed class AgentMemoryDatabase
{
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

    public AgentMemoryDatabase(IConfiguration configuration, ILogger<AgentMemoryDatabase> logger)
    {
        _logger = logger;
        string databasePath = configuration["ConversationDatabase:Path"] ?? "agent_memory.db";
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        Initialize();
    }

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
