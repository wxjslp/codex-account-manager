namespace CodexAccountManager.Core;

public sealed class OperationLogService
{
    private readonly Database _database;

    public OperationLogService(Database database)
    {
        _database = database;
    }

    public void Info(string action, string message, string? detail = null) =>
        Write("info", action, message, detail);

    public void Error(string action, string message, string? detail = null) =>
        Write("error", action, message, detail);

    public IReadOnlyList<OperationLogEntry> Recent(int limit = 80)
    {
        return _database.Query(
            """
            SELECT id, level, action, message, detail, created_at
            FROM operation_logs
            ORDER BY created_at DESC
            LIMIT $limit
            """,
            reader => new OperationLogEntry(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                DateTimeOffset.Parse(reader.GetString(5))),
            Database.Param("$limit", limit));
    }

    public void Clear()
    {
        _database.Execute("DELETE FROM operation_logs");
    }

    private void Write(string level, string action, string message, string? detail)
    {
        _database.Execute(
            """
            INSERT INTO operation_logs (id, level, action, message, detail, created_at)
            VALUES ($id, $level, $action, $message, $detail, $created_at)
            """,
            Database.Param("$id", Guid.NewGuid().ToString("N")),
            Database.Param("$level", level),
            Database.Param("$action", action),
            Database.Param("$message", SecretRedactor.Redact(message)),
            Database.Param("$detail", SecretRedactor.Redact(detail)),
            Database.Param("$created_at", DateTimeOffset.UtcNow.ToString("O")));
    }
}
