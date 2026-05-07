using Microsoft.Data.Sqlite;

namespace CodexAccountManager.Core;

public sealed class Database
{
    private const string TablesSchema = """
CREATE TABLE IF NOT EXISTS accounts (
    id TEXT PRIMARY KEY,
    profile_key TEXT NOT NULL UNIQUE,
    account_id TEXT,
    display_name TEXT NOT NULL,
    email TEXT,
    plan_type TEXT,
    auth_mode TEXT,
    profile_home TEXT NOT NULL UNIQUE,
    auth_path TEXT,
    is_active INTEGER NOT NULL DEFAULT 0,
    status TEXT NOT NULL DEFAULT 'missing_auth',
    status_message TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_status_check_at TEXT,
    last_switch_at TEXT
);

CREATE TABLE IF NOT EXISTS operation_logs (
    id TEXT PRIMARY KEY,
    level TEXT NOT NULL,
    action TEXT NOT NULL,
    message TEXT NOT NULL,
    detail TEXT,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS backup_records (
    id TEXT PRIMARY KEY,
    account_id TEXT NOT NULL,
    backup_path TEXT NOT NULL,
    reason TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE TABLE IF NOT EXISTS account_rate_limits (
    account_id TEXT NOT NULL,
    limit_id TEXT NOT NULL,
    limit_name TEXT,
    plan_type TEXT,
    primary_used_percent REAL,
    primary_window_duration_mins INTEGER,
    primary_resets_at TEXT,
    secondary_used_percent REAL,
    secondary_window_duration_mins INTEGER,
    secondary_resets_at TEXT,
    rate_limit_reached_type TEXT,
    credits_json TEXT,
    checked_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (account_id, limit_id)
);
""";

    private const string IndexSchema = """
CREATE INDEX IF NOT EXISTS idx_accounts_email ON accounts(email);
CREATE INDEX IF NOT EXISTS idx_accounts_active ON accounts(is_active);
CREATE UNIQUE INDEX IF NOT EXISTS idx_accounts_profile_key ON accounts(profile_key);
CREATE INDEX IF NOT EXISTS idx_operation_logs_created_at ON operation_logs(created_at DESC);
CREATE INDEX IF NOT EXISTS idx_backup_records_account_id ON backup_records(account_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_account_rate_limits_account_id ON account_rate_limits(account_id, checked_at DESC);
""";

    private readonly string _connectionString;

    public Database(ManagerPaths paths)
    {
        Paths = paths;
        Directory.CreateDirectory(Path.GetDirectoryName(paths.DatabasePath)!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString();
        Initialize();
    }

    public ManagerPaths Paths { get; }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    public void Execute(string sql, params SqliteParameter[] parameters)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        command.ExecuteNonQuery();
    }

    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    public T? QuerySingle<T>(string sql, Func<SqliteDataReader, T> map, params SqliteParameter[] parameters)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        using var reader = command.ExecuteReader();
        return reader.Read() ? map(reader) : default;
    }

    public void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        work(connection, transaction);
        transaction.Commit();
    }

    public static SqliteParameter Param(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = TablesSchema;
        command.ExecuteNonQuery();
        ApplyMigrations(connection);
        command.CommandText = IndexSchema;
        command.ExecuteNonQuery();
    }

    private static void ApplyMigrations(SqliteConnection connection)
    {
        EnsureColumn(connection, "accounts", "profile_key", "TEXT");
        EnsureColumn(connection, "accounts", "account_id", "TEXT");
        EnsureColumn(connection, "accounts", "email", "TEXT");
        EnsureColumn(connection, "accounts", "plan_type", "TEXT");
        EnsureColumn(connection, "accounts", "auth_mode", "TEXT");
        EnsureColumn(connection, "accounts", "auth_path", "TEXT");
        EnsureColumn(connection, "accounts", "status", "TEXT NOT NULL DEFAULT 'missing_auth'");
        EnsureColumn(connection, "accounts", "status_message", "TEXT");
        EnsureColumn(connection, "accounts", "last_status_check_at", "TEXT");
        EnsureColumn(connection, "accounts", "last_switch_at", "TEXT");
        EnsureColumn(connection, "operation_logs", "detail", "TEXT");
        EnsureColumn(connection, "backup_records", "reason", "TEXT NOT NULL DEFAULT 'manual'");
        BackfillProfileKeys(connection);
        NormalizeAccountStatus(connection);
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string definition)
    {
        if (ColumnExists(connection, table, column))
        {
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
        command.ExecuteNonQuery();
    }

    private static bool ColumnExists(SqliteConnection connection, string table, string column)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void BackfillProfileKeys(SqliteConnection connection)
    {
        var rows = new List<(string Id, string DisplayName, string? ProfileKey)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, display_name, profile_key FROM accounts ORDER BY created_at, id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var baseKey = string.IsNullOrWhiteSpace(row.ProfileKey)
                ? FileSystemProfileStore.SafeSlug(row.DisplayName)
                : FileSystemProfileStore.SafeSlug(row.ProfileKey);
            if (string.IsNullOrWhiteSpace(baseKey))
            {
                baseKey = $"profile-{row.Id[..Math.Min(row.Id.Length, 8)]}";
            }

            var key = baseKey;
            var index = 2;
            while (!used.Add(key))
            {
                key = $"{baseKey}-{index++}";
            }

            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE accounts SET profile_key = $profile_key WHERE id = $id";
            command.Parameters.AddWithValue("$profile_key", key);
            command.Parameters.AddWithValue("$id", row.Id);
            command.ExecuteNonQuery();
        }
    }

    private static void NormalizeAccountStatus(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE accounts
            SET status = 'missing_auth',
                status_message = COALESCE(status_message, '未找到 auth.json')
            WHERE (auth_path IS NULL OR auth_path = '')
              AND (status IS NULL OR status = '' OR status = 'ok')
            """;
        command.ExecuteNonQuery();
    }
}
