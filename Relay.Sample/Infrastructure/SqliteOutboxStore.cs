using Microsoft.Data.Sqlite;
using Relay.Outbox.Core;

namespace Relay.Sample.Infrastructure;

/// <summary>
/// IOutboxStore backed by SQLite via Microsoft.Data.Sqlite.
/// Schema auto-created on startup via EnsureSchemaAsync (called by SqliteSchemaInitializer).
/// </summary>
public sealed class SqliteOutboxStore(string connectionString) : IOutboxStore
{
    // -------------------------------------------------------------------------
    // Schema
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        const string sql = """
            CREATE TABLE IF NOT EXISTS OutboxMessages (
                Id              TEXT    NOT NULL PRIMARY KEY,
                OutboxName      TEXT    NOT NULL,
                Type            TEXT    NOT NULL,
                Payload         TEXT    NOT NULL,
                CorrelationId   TEXT    NULL,
                TraceId         TEXT    NULL,
                Destination     TEXT    NULL,
                Status          INTEGER NOT NULL DEFAULT 0,
                CreatedAt       TEXT    NOT NULL,
                PublishedAt     TEXT    NULL,
                Error           TEXT    NULL,
                RetryCount      INTEGER NOT NULL DEFAULT 0,
                ScheduledFor    TEXT    NULL
            );

            CREATE INDEX IF NOT EXISTS IX_OutboxMessages_OutboxName_Status
                ON OutboxMessages (OutboxName, Status, CreatedAt);

            CREATE INDEX IF NOT EXISTS IX_OutboxMessages_CorrelationId
                ON OutboxMessages (CorrelationId)
                WHERE CorrelationId IS NOT NULL;
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Write path
    // -------------------------------------------------------------------------

    public async Task InsertAsync(OutboxMessage message, CancellationToken ct = default)
    {
        const string sql = """
            INSERT INTO OutboxMessages
                (Id, OutboxName, Type, Payload, CorrelationId, TraceId,
                 Destination, Status, CreatedAt, ScheduledFor)
            VALUES
                (@id, @outboxName, @type, @payload, @correlationId, @traceId,
                 @destination, @status, @createdAt, @scheduledFor)
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",            message.Id.ToString());
        cmd.Parameters.AddWithValue("@outboxName",    message.OutboxName);
        cmd.Parameters.AddWithValue("@type",          message.Type);
        cmd.Parameters.AddWithValue("@payload",       message.Payload);
        cmd.Parameters.AddWithValue("@correlationId", (object?)message.CorrelationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@traceId",       (object?)message.TraceId       ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@destination",   (object?)message.Destination   ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status",        (int)message.Status);
        cmd.Parameters.AddWithValue("@createdAt",     message.CreatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@scheduledFor",  message.ScheduledFor.HasValue
            ? (object)message.ScheduledFor.Value.ToString("O")
            : DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Read path — LIMIT, datetime('now') instead of SYSUTCDATETIME(), no hints
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
        string outboxName, int batchSize, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, OutboxName, Type, Payload, CorrelationId, TraceId,
                   Destination, Status, CreatedAt, PublishedAt, Error, RetryCount, ScheduledFor
            FROM OutboxMessages
            WHERE OutboxName = @outbox
              AND Status IN ({(int)OutboxMessageStatus.Pending}, {(int)OutboxMessageStatus.Failed})
              AND (ScheduledFor IS NULL OR ScheduledFor <= datetime('now'))
            ORDER BY CreatedAt ASC
            LIMIT @batch
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@outbox", outboxName);
        cmd.Parameters.AddWithValue("@batch",  batchSize);
        return await ReadMessagesAsync(cmd, ct);
    }

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    public Task MarkDispatchingAsync(Guid id, CancellationToken ct = default) =>
        SetStatusAsync(id, OutboxMessageStatus.Dispatching, ct);

    public Task MarkPublishedAsync(Guid id, CancellationToken ct = default) =>
        SetStatusAsync(id, OutboxMessageStatus.Published, ct, publishedAt: DateTime.UtcNow);

    public async Task MarkFailedAsync(Guid id, string error, int retryCount, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE OutboxMessages
            SET Status = @status, Error = @error, RetryCount = @retry
            WHERE Id = @id
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",     id.ToString());
        cmd.Parameters.AddWithValue("@status", (int)OutboxMessageStatus.Failed);
        cmd.Parameters.AddWithValue("@error",  error);
        cmd.Parameters.AddWithValue("@retry",  retryCount);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default)
    {
        const string sql = "UPDATE OutboxMessages SET Status = @status, Error = @error WHERE Id = @id";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",     id.ToString());
        cmd.Parameters.AddWithValue("@status", (int)OutboxMessageStatus.DeadLettered);
        cmd.Parameters.AddWithValue("@error",  error);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IOutboxQuery — separate commands per query (SQLite multi-result limitation)
    // -------------------------------------------------------------------------

    public async Task<OutboxStats> GetStatsAsync(string outboxName, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);

        var counts = new Dictionary<OutboxMessageStatus, int>();
        const string countSql = """
            SELECT Status, COUNT(*) FROM OutboxMessages
            WHERE OutboxName = @outbox GROUP BY Status
            """;
        await using (var cmd = new SqliteCommand(countSql, conn))
        {
            cmd.Parameters.AddWithValue("@outbox", outboxName);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                counts[(OutboxMessageStatus)r.GetInt32(0)] = r.GetInt32(1);
        }

        DateTime? oldestPending = null;
        var oldestSql = $"""
            SELECT MIN(CreatedAt) FROM OutboxMessages
            WHERE OutboxName = @outbox AND Status = {(int)OutboxMessageStatus.Pending}
              AND (ScheduledFor IS NULL OR ScheduledFor <= datetime('now'))
            """;
        await using (var cmd = new SqliteCommand(oldestSql, conn))
        {
            cmd.Parameters.AddWithValue("@outbox", outboxName);
            if (await cmd.ExecuteScalarAsync(ct) is string s)
                oldestPending = DateTime.Parse(s);
        }

        var scheduled = 0;
        var scheduledSql = $"""
            SELECT COUNT(*) FROM OutboxMessages
            WHERE OutboxName = @outbox
              AND Status = {(int)OutboxMessageStatus.Pending}
              AND ScheduledFor > datetime('now')
            """;
        await using (var cmd = new SqliteCommand(scheduledSql, conn))
        {
            cmd.Parameters.AddWithValue("@outbox", outboxName);
            if (await cmd.ExecuteScalarAsync(ct) is long l) scheduled = (int)l;
        }

        return new OutboxStats(
            OutboxName:    outboxName,
            Pending:       counts.GetValueOrDefault(OutboxMessageStatus.Pending),
            Dispatching:   counts.GetValueOrDefault(OutboxMessageStatus.Dispatching),
            Published:     counts.GetValueOrDefault(OutboxMessageStatus.Published),
            Failed:        counts.GetValueOrDefault(OutboxMessageStatus.Failed),
            DeadLettered:  counts.GetValueOrDefault(OutboxMessageStatus.DeadLettered),
            Scheduled:     scheduled,
            OldestPending: oldestPending);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(
        string outboxName, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, OutboxName, Type, Payload, CorrelationId, TraceId,
                   Destination, Status, CreatedAt, PublishedAt, Error, RetryCount, ScheduledFor
            FROM OutboxMessages
            WHERE OutboxName = @outbox AND Status = {(int)OutboxMessageStatus.DeadLettered}
            ORDER BY CreatedAt DESC
            LIMIT @limit
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@outbox", outboxName);
        cmd.Parameters.AddWithValue("@limit",  limit);
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetFailedAsync(
        string outboxName, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, OutboxName, Type, Payload, CorrelationId, TraceId,
                   Destination, Status, CreatedAt, PublishedAt, Error, RetryCount, ScheduledFor
            FROM OutboxMessages
            WHERE OutboxName = @outbox AND Status = {(int)OutboxMessageStatus.Failed}
            ORDER BY CreatedAt DESC
            LIMIT @limit
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@outbox", outboxName);
        cmd.Parameters.AddWithValue("@limit",  limit);
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetByCorrelationIdAsync(
        string correlationId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id, OutboxName, Type, Payload, CorrelationId, TraceId,
                   Destination, Status, CreatedAt, PublishedAt, Error, RetryCount, ScheduledFor
            FROM OutboxMessages
            WHERE CorrelationId = @correlationId
            ORDER BY CreatedAt ASC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@correlationId", correlationId);
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<int> PurgePublishedAsync(
        string outboxName, TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = (DateTime.UtcNow - olderThan).ToString("O");
        var sql = $"""
            DELETE FROM OutboxMessages
            WHERE OutboxName = @outbox
              AND Status = {(int)OutboxMessageStatus.Published}
              AND PublishedAt < @cutoff
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@outbox",  outboxName);
        cmd.Parameters.AddWithValue("@cutoff",  cutoff);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IOutboxRequeue
    // -------------------------------------------------------------------------

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE OutboxMessages
            SET Status = {(int)OutboxMessageStatus.Pending}, Error = NULL, RetryCount = 0
            WHERE Id = @id
              AND Status IN ({(int)OutboxMessageStatus.DeadLettered}, {(int)OutboxMessageStatus.Failed})
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", messageId.ToString());
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> RequeueAllDeadLetteredAsync(string outboxName, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE OutboxMessages
            SET Status = {(int)OutboxMessageStatus.Pending}, Error = NULL, RetryCount = 0
            WHERE OutboxName = @outbox
              AND Status = {(int)OutboxMessageStatus.DeadLettered}
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@outbox", outboxName);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task SetStatusAsync(
        Guid id, OutboxMessageStatus status, CancellationToken ct, DateTime? publishedAt = null)
    {
        var sql = publishedAt.HasValue
            ? "UPDATE OutboxMessages SET Status = @status, PublishedAt = @ts WHERE Id = @id"
            : "UPDATE OutboxMessages SET Status = @status WHERE Id = @id";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id",     id.ToString());
        cmd.Parameters.AddWithValue("@status", (int)status);
        if (publishedAt.HasValue)
            cmd.Parameters.AddWithValue("@ts", publishedAt.Value.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<OutboxMessage>> ReadMessagesAsync(
        SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<OutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapRow(reader));
        return list;
    }

    private static OutboxMessage MapRow(SqliteDataReader r) => new()
    {
        Id            = Guid.Parse(r.GetString(0)),
        OutboxName    = r.GetString(1),
        Type          = r.GetString(2),
        Payload       = r.GetString(3),
        CorrelationId = r.IsDBNull(4)  ? null : r.GetString(4),
        TraceId       = r.IsDBNull(5)  ? null : r.GetString(5),
        Destination   = r.IsDBNull(6)  ? null : r.GetString(6),
        Status        = (OutboxMessageStatus)r.GetInt32(7),
        CreatedAt     = DateTime.Parse(r.GetString(8)),
        PublishedAt   = r.IsDBNull(9)  ? null : DateTime.Parse(r.GetString(9)),
        Error         = r.IsDBNull(10) ? null : r.GetString(10),
        RetryCount    = r.GetInt32(11),
        ScheduledFor  = r.IsDBNull(12) ? null : DateTime.Parse(r.GetString(12)),
    };

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
