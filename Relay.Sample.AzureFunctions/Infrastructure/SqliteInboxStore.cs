using Microsoft.Data.Sqlite;
using Relay.Inbox.Core;

namespace Relay.Sample.AzureFunctions.Infrastructure;

/// <summary>
/// IInboxStore backed by SQLite via Microsoft.Data.Sqlite.
/// Schema auto-created on startup via EnsureSchemaAsync (called by SqliteSchemaInitializer).
/// </summary>
public sealed class SqliteInboxStore(string connectionString, string tableName = "InboxMessages") : IInboxStore
{
    private readonly string _table = tableName;
    // -------------------------------------------------------------------------
    // Schema
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        // SQLite stores GUIDs and datetimes as ISO-8601 TEXT.
        // No UNIQUEIDENTIFIER, NVARCHAR or SQL Server hints needed.
        var sql = $"""
            CREATE TABLE IF NOT EXISTS [{_table}] (
                Id              TEXT    NOT NULL PRIMARY KEY,
                InboxName       TEXT    NOT NULL,
                Type            TEXT    NOT NULL,
                IdempotencyKey  TEXT    NOT NULL UNIQUE,
                Payload         TEXT    NOT NULL,
                Status          INTEGER NOT NULL DEFAULT 0,
                ReceivedAt      TEXT    NOT NULL,
                ProcessedAt     TEXT    NULL,
                Error           TEXT    NULL,
                RetryCount      INTEGER NOT NULL DEFAULT 0,
                TraceId         TEXT    NULL,
                Source          TEXT    NULL,
                SourceTimestamp TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS IX_{_table}_InboxName_Status
                ON [{_table}] (InboxName, Status, ReceivedAt);
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);

        // Add SourceTimestamp to existing tables that predate this column.
        // Use pragma_table_info rather than ALTER TABLE + catch, as SQLite may throw
        // NullReferenceException or InvalidOperationException in some hosting environments.
        await using var checkCmd = new SqliteCommand(
            $"SELECT COUNT(*) FROM pragma_table_info('{_table}') WHERE name='SourceTimestamp'", conn);
        var colExists = (long)(await checkCmd.ExecuteScalarAsync(ct))! > 0;
        if (!colExists)
        {
            await using var alter = new SqliteCommand(
                $"ALTER TABLE [{_table}] ADD COLUMN SourceTimestamp TEXT NULL", conn);
            await alter.ExecuteNonQueryAsync(ct);
        }
    }

    // -------------------------------------------------------------------------
    // Write path
    // -------------------------------------------------------------------------

    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
    {
        var sql = $"SELECT 1 FROM [{_table}] WHERE IdempotencyKey = @key";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@key", idempotencyKey);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<(Guid Id, DateTime? SourceTimestamp)?> TryGetAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var sql = $"SELECT Id, SourceTimestamp FROM [{_table}] WHERE IdempotencyKey = @key";
        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@key", idempotencyKey);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var id = Guid.Parse(reader.GetString(0));
        DateTime? sourceTimestamp = reader.IsDBNull(1) ? null : DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
        return (id, sourceTimestamp);
    }

    public async Task<bool> UpdateIfNewerAsync(
        string idempotencyKey, string payload, DateTime sourceTimestamp, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE [{_table}]
            SET Payload         = @payload,
                SourceTimestamp = @sourceTs,
                Status          = {(int)InboxMessageStatus.Pending},
                ReceivedAt      = @now,
                Error           = NULL,
                RetryCount      = 0,
                ProcessedAt     = NULL
            WHERE IdempotencyKey = @key
              AND (SourceTimestamp IS NULL OR SourceTimestamp < @sourceTs)
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@key",      idempotencyKey);
        cmd.Parameters.AddWithValue("@payload",  payload);
        cmd.Parameters.AddWithValue("@sourceTs", sourceTimestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@now",      DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task InsertAsync(InboxMessage message, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO [{_table}]
                (Id, InboxName, Type, IdempotencyKey, Payload, Status, ReceivedAt, TraceId, Source, SourceTimestamp)
            VALUES
                (@id, @inboxName, @type, @key, @payload, @status, @receivedAt, @traceId, @source, @sourceTs)
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",       message.Id.ToString());
        cmd.Parameters.AddWithValue("@inboxName", message.InboxName);
        cmd.Parameters.AddWithValue("@type",     message.Type);
        cmd.Parameters.AddWithValue("@key",      message.IdempotencyKey);
        cmd.Parameters.AddWithValue("@payload",  message.Payload);
        cmd.Parameters.AddWithValue("@status",   (int)message.Status);
        cmd.Parameters.AddWithValue("@receivedAt", message.ReceivedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@traceId",  (object?)message.TraceId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@source",   (object?)message.Source ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@sourceTs", (object?)message.SourceTimestamp?.ToString("O") ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Read path — LIMIT instead of TOP, no table hints
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<InboxMessage>> GetPendingAsync(
        string inboxName, int batchSize, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, InboxName, Type, IdempotencyKey, Payload,
                   Status, ReceivedAt, ProcessedAt, Error, RetryCount, TraceId, Source, SourceTimestamp
            FROM [{_table}]
            WHERE InboxName = @inbox
              AND Status IN ({(int)InboxMessageStatus.Pending}, {(int)InboxMessageStatus.Failed})
            ORDER BY ReceivedAt ASC
            LIMIT @batch
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@inbox", inboxName);
        cmd.Parameters.AddWithValue("@batch", batchSize);
        return await ReadMessagesAsync(cmd, ct);
    }

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    public Task MarkProcessingAsync(Guid id, CancellationToken ct = default) =>
        SetStatusAsync(id, InboxMessageStatus.Processing, ct);

    public Task MarkProcessedAsync(Guid id, CancellationToken ct = default) =>
        SetStatusAsync(id, InboxMessageStatus.Processed, ct, processedAt: DateTime.UtcNow);

    public async Task MarkFailedAsync(Guid id, string error, int retryCount, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE [{_table}]
            SET Status = @status, Error = @error, RetryCount = @retry
            WHERE Id = @id
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@status", (int)InboxMessageStatus.Failed);
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@retry", retryCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default)
    {
        var sql = $"UPDATE [{_table}] SET Status = @status, Error = @error WHERE Id = @id";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@status", (int)InboxMessageStatus.DeadLettered);
        cmd.Parameters.AddWithValue("@error", error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IInboxQuery — stats, dead-letters, purge
    // SQLite doesn't support multi-result batches reliably, so use separate cmds.
    // -------------------------------------------------------------------------

    public async Task<InboxStats> GetStatsAsync(string inboxName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        var counts = new Dictionary<InboxMessageStatus, int>();
        var countSql = $"""
            SELECT Status, COUNT(*) FROM [{_table}]
            WHERE InboxName = @inbox GROUP BY Status
            """;
        await using (var cmd = new SqliteCommand(countSql, conn))
        {
            cmd.Parameters.AddWithValue("@inbox", inboxName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                counts[(InboxMessageStatus)r.GetInt32(0)] = r.GetInt32(1);
        }

        DateTime? oldestPending = null;
        var oldestSql = $"""
            SELECT MIN(ReceivedAt) FROM [{_table}]
            WHERE InboxName = @inbox AND Status = {(int)InboxMessageStatus.Pending}
            """;
        await using (var cmd = new SqliteCommand(oldestSql, conn))
        {
            cmd.Parameters.AddWithValue("@inbox", inboxName);
            if (await cmd.ExecuteScalarAsync(ct) is string s)
                oldestPending = DateTime.Parse(s);
        }

        return new InboxStats(
            InboxName: inboxName,
            Pending: counts.GetValueOrDefault(InboxMessageStatus.Pending),
            Processing: counts.GetValueOrDefault(InboxMessageStatus.Processing),
            Processed: counts.GetValueOrDefault(InboxMessageStatus.Processed),
            Failed: counts.GetValueOrDefault(InboxMessageStatus.Failed),
            DeadLettered: counts.GetValueOrDefault(InboxMessageStatus.DeadLettered),
            OldestPending: oldestPending);
    }

    public async Task<IReadOnlyList<InboxMessage>> GetDeadLetteredAsync(
        string inboxName, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, InboxName, Type, IdempotencyKey, Payload,
                   Status, ReceivedAt, ProcessedAt, Error, RetryCount, TraceId, Source, SourceTimestamp
            FROM [{_table}]
            WHERE InboxName = @inbox AND Status = {(int)InboxMessageStatus.DeadLettered}
            ORDER BY ReceivedAt DESC
            LIMIT @limit
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@inbox", inboxName);
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<InboxMessage>> GetFailedAsync(
        string inboxName, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, InboxName, Type, IdempotencyKey, Payload,
                   Status, ReceivedAt, ProcessedAt, Error, RetryCount, TraceId, Source, SourceTimestamp
            FROM [{_table}]
            WHERE InboxName = @inbox AND Status = {(int)InboxMessageStatus.Failed}
            ORDER BY ReceivedAt DESC
            LIMIT @limit
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@inbox", inboxName);
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<int> PurgeProcessedAsync(
        string inboxName, TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = (DateTime.UtcNow - olderThan).ToString("O");
        var sql = $"""
            DELETE FROM [{_table}]
            WHERE InboxName = @inbox
              AND Status = {(int)InboxMessageStatus.Processed}
              AND ProcessedAt < @cutoff
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@inbox", inboxName);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IInboxRequeue
    // -------------------------------------------------------------------------

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE [{_table}]
            SET Status = {(int)InboxMessageStatus.Pending}, Error = NULL, RetryCount = 0
            WHERE Id = @id
              AND Status IN ({(int)InboxMessageStatus.DeadLettered}, {(int)InboxMessageStatus.Failed})
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", messageId.ToString());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> RequeueAllDeadLetteredAsync(string inboxName, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE [{_table}]
            SET Status = {(int)InboxMessageStatus.Pending}, Error = NULL, RetryCount = 0
            WHERE InboxName = @inbox
              AND Status = {(int)InboxMessageStatus.DeadLettered}
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@inbox", inboxName);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task SetStatusAsync(
        Guid id, InboxMessageStatus status, CancellationToken ct, DateTime? processedAt = null)
    {
        var sql = processedAt.HasValue
            ? $"UPDATE [{_table}] SET Status = @status, ProcessedAt = @ts WHERE Id = @id"
            : $"UPDATE [{_table}] SET Status = @status WHERE Id = @id";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", id.ToString());
        cmd.Parameters.AddWithValue("@status", (int)status);
        if (processedAt.HasValue)
            cmd.Parameters.AddWithValue("@ts", processedAt.Value.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<InboxMessage>> ReadMessagesAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<InboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapRow(reader));
        return list;
    }

    private static InboxMessage MapRow(SqliteDataReader r) => new()
    {
        Id = Guid.Parse(r.GetString(0)),
        InboxName = r.GetString(1),
        Type = r.GetString(2),
        IdempotencyKey = r.GetString(3),
        Payload = r.GetString(4),
        Status = (InboxMessageStatus)r.GetInt32(5),
        ReceivedAt = DateTime.Parse(r.GetString(6)),
        ProcessedAt = r.IsDBNull(7) ? null : DateTime.Parse(r.GetString(7)),
        Error = r.IsDBNull(8) ? null : r.GetString(8),
        RetryCount = r.GetInt32(9),
        TraceId         = r.IsDBNull(10) ? null : r.GetString(10),
        Source          = r.IsDBNull(11) ? null : r.GetString(11),
        SourceTimestamp = r.IsDBNull(12) ? null : DateTime.Parse(r.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind),
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
