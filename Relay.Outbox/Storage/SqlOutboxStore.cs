using System.Data;
using Microsoft.Data.SqlClient;
using Relay.Outbox.Core;

namespace Relay.Outbox.Storage;

/// <summary>
/// SQL Server outbox store using raw ADO.NET (Microsoft.Data.SqlClient).
/// No ORM. Supports SKIP LOCKED for safe parallel dispatch.
/// </summary>
public sealed class SqlOutboxStore(SqlOutboxStoreOptions options) : IOutboxStore
{
    private readonly string _table = options.TableName;

    // -------------------------------------------------------------------------
    // Schema
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        var sql = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = '{_table}')
            BEGIN
                CREATE TABLE [{_table}] (
                    Id              UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
                    OutboxName      NVARCHAR(100)       NOT NULL,
                    [Type]          NVARCHAR(250)       NOT NULL,
                    Payload         NVARCHAR(MAX)       NOT NULL,
                    CorrelationId   NVARCHAR(100)       NULL,
                    TraceId         NVARCHAR(100)       NULL,
                    Destination     NVARCHAR(500)       NULL,
                    Status          TINYINT             NOT NULL DEFAULT 0,
                    CreatedAt       DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
                    PublishedAt     DATETIME2           NULL,
                    Error           NVARCHAR(2000)      NULL,
                    RetryCount      INT                 NOT NULL DEFAULT 0,
                    ScheduledFor    DATETIME2           NULL
                );

                CREATE NONCLUSTERED INDEX IX_{_table}_OutboxName_Status_CreatedAt
                    ON [{_table}] (OutboxName, Status, CreatedAt)
                    INCLUDE (Id, [Type], ScheduledFor);

                CREATE NONCLUSTERED INDEX IX_{_table}_CorrelationId
                    ON [{_table}] (CorrelationId)
                    WHERE CorrelationId IS NOT NULL;
            END
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Write path
    // -------------------------------------------------------------------------

    public async Task InsertAsync(OutboxMessage message, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO [{_table}]
                (Id, OutboxName, [Type], Payload, CorrelationId, TraceId,
                 Destination, Status, CreatedAt, ScheduledFor)
            VALUES
                (@id, @outboxName, @type, @payload, @correlationId, @traceId,
                 @destination, @status, @createdAt, @scheduledFor)
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",            SqlDbType.UniqueIdentifier).Value = message.Id;
        cmd.Parameters.Add("@outboxName",    SqlDbType.NVarChar, 100).Value    = message.OutboxName;
        cmd.Parameters.Add("@type",          SqlDbType.NVarChar, 250).Value    = message.Type;
        cmd.Parameters.Add("@payload",       SqlDbType.NVarChar, -1).Value     = message.Payload;
        cmd.Parameters.Add("@correlationId", SqlDbType.NVarChar, 100).Value    = (object?)message.CorrelationId ?? DBNull.Value;
        cmd.Parameters.Add("@traceId",       SqlDbType.NVarChar, 100).Value    = (object?)message.TraceId       ?? DBNull.Value;
        cmd.Parameters.Add("@destination",   SqlDbType.NVarChar, 500).Value    = (object?)message.Destination   ?? DBNull.Value;
        cmd.Parameters.Add("@status",        SqlDbType.TinyInt).Value          = (int)message.Status;
        cmd.Parameters.Add("@createdAt",     SqlDbType.DateTime2).Value        = message.CreatedAt;
        cmd.Parameters.Add("@scheduledFor",  SqlDbType.DateTime2).Value        = (object?)message.ScheduledFor  ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Read path
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
        string outboxName, int batchSize, CancellationToken ct = default)
    {
        var skipLocked = options.UseSkipLocked ? "WITH (UPDLOCK, READPAST)" : "WITH (UPDLOCK)";

        var sql = $"""
            SELECT TOP (@batch) Id, OutboxName, [Type], Payload, CorrelationId, TraceId,
                                Destination, Status, CreatedAt, PublishedAt, Error,
                                RetryCount, ScheduledFor
            FROM [{_table}] {skipLocked}
            WHERE OutboxName = @outbox
              AND Status IN ({(int)OutboxMessageStatus.Pending}, {(int)OutboxMessageStatus.Failed})
              AND (ScheduledFor IS NULL OR ScheduledFor <= SYSUTCDATETIME())
            ORDER BY CreatedAt ASC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@batch",  SqlDbType.Int).Value           = batchSize;
        cmd.Parameters.Add("@outbox", SqlDbType.NVarChar, 100).Value = outboxName;

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
        var sql = $"""
            UPDATE [{_table}]
            SET Status = @status, Error = @error, RetryCount = @retry
            WHERE Id = @id
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",     SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value          = (int)OutboxMessageStatus.Failed;
        cmd.Parameters.Add("@error",  SqlDbType.NVarChar, 2000).Value   = error;
        cmd.Parameters.Add("@retry",  SqlDbType.Int).Value              = retryCount;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default)
    {
        var sql = $"UPDATE [{_table}] SET Status = @status, Error = @error WHERE Id = @id";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",     SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value          = (int)OutboxMessageStatus.DeadLettered;
        cmd.Parameters.Add("@error",  SqlDbType.NVarChar, 2000).Value   = error;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IOutboxQuery
    // -------------------------------------------------------------------------

    public async Task<OutboxStats> GetStatsAsync(string outboxName, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Status, COUNT(*) AS Cnt
            FROM [{_table}]
            WHERE OutboxName = @outbox
            GROUP BY Status;

            SELECT MIN(CreatedAt)
            FROM [{_table}]
            WHERE OutboxName = @outbox AND Status = {(int)OutboxMessageStatus.Pending}
              AND (ScheduledFor IS NULL OR ScheduledFor <= SYSUTCDATETIME());

            SELECT COUNT(*)
            FROM [{_table}]
            WHERE OutboxName = @outbox
              AND Status = {(int)OutboxMessageStatus.Pending}
              AND ScheduledFor > SYSUTCDATETIME();
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@outbox", SqlDbType.NVarChar, 100).Value = outboxName;

        var counts = new Dictionary<OutboxMessageStatus, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            counts[(OutboxMessageStatus)reader.GetByte(0)] = reader.GetInt32(1);

        await reader.NextResultAsync(ct);
        DateTime? oldestPending = null;
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
            oldestPending = reader.GetDateTime(0);

        await reader.NextResultAsync(ct);
        var scheduled = 0;
        if (await reader.ReadAsync(ct)) scheduled = reader.GetInt32(0);

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
            SELECT TOP (@limit) Id, OutboxName, [Type], Payload, CorrelationId, TraceId,
                                Destination, Status, CreatedAt, PublishedAt, Error,
                                RetryCount, ScheduledFor
            FROM [{_table}]
            WHERE OutboxName = @outbox AND Status = {(int)OutboxMessageStatus.DeadLettered}
            ORDER BY CreatedAt DESC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@outbox", SqlDbType.NVarChar, 100).Value = outboxName;
        cmd.Parameters.Add("@limit",  SqlDbType.Int).Value           = limit;
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetFailedAsync(
        string outboxName, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT TOP (@limit) Id, OutboxName, [Type], Payload, CorrelationId, TraceId,
                                Destination, Status, CreatedAt, PublishedAt, Error,
                                RetryCount, ScheduledFor
            FROM [{_table}]
            WHERE OutboxName = @outbox AND Status = {(int)OutboxMessageStatus.Failed}
            ORDER BY CreatedAt DESC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@outbox", SqlDbType.NVarChar, 100).Value = outboxName;
        cmd.Parameters.Add("@limit",  SqlDbType.Int).Value           = limit;
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetByCorrelationIdAsync(
        string correlationId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, OutboxName, [Type], Payload, CorrelationId, TraceId,
                   Destination, Status, CreatedAt, PublishedAt, Error,
                   RetryCount, ScheduledFor
            FROM [{_table}]
            WHERE CorrelationId = @correlationId
            ORDER BY CreatedAt ASC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@correlationId", SqlDbType.NVarChar, 100).Value = correlationId;
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<int> PurgePublishedAsync(
        string outboxName, TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var sql = $"""
            DELETE FROM [{_table}]
            WHERE OutboxName = @outbox
              AND Status = {(int)OutboxMessageStatus.Published}
              AND PublishedAt < @cutoff
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@outbox",  SqlDbType.NVarChar, 100).Value = outboxName;
        cmd.Parameters.Add("@cutoff",  SqlDbType.DateTime2).Value     = cutoff;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IOutboxRequeue
    // -------------------------------------------------------------------------

    public async Task<bool> RequeueAsync(Guid messageId, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE [{_table}]
            SET Status = {(int)OutboxMessageStatus.Pending}, Error = NULL, RetryCount = 0
            WHERE Id = @id
              AND Status IN ({(int)OutboxMessageStatus.DeadLettered}, {(int)OutboxMessageStatus.Failed})
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = messageId;
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<int> RequeueAllDeadLetteredAsync(
        string outboxName, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE [{_table}]
            SET Status = {(int)OutboxMessageStatus.Pending}, Error = NULL, RetryCount = 0
            WHERE OutboxName = @outbox
              AND Status = {(int)OutboxMessageStatus.DeadLettered}
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@outbox", SqlDbType.NVarChar, 100).Value = outboxName;
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task SetStatusAsync(
        Guid id, OutboxMessageStatus status, CancellationToken ct, DateTime? publishedAt = null)
    {
        var sql = publishedAt.HasValue
            ? $"UPDATE [{_table}] SET Status = @status, PublishedAt = @ts WHERE Id = @id"
            : $"UPDATE [{_table}] SET Status = @status WHERE Id = @id";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",     SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value          = (int)status;
        if (publishedAt.HasValue)
            cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = publishedAt.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<OutboxMessage>> ReadMessagesAsync(
        SqlCommand cmd, CancellationToken ct)
    {
        var list = new List<OutboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapRow(reader));
        return list;
    }

    private static OutboxMessage MapRow(SqlDataReader r) => new()
    {
        Id            = r.GetGuid(0),
        OutboxName    = r.GetString(1),
        Type          = r.GetString(2),
        Payload       = r.GetString(3),
        CorrelationId = r.IsDBNull(4)  ? null : r.GetString(4),
        TraceId       = r.IsDBNull(5)  ? null : r.GetString(5),
        Destination   = r.IsDBNull(6)  ? null : r.GetString(6),
        Status        = (OutboxMessageStatus)r.GetByte(7),
        CreatedAt     = r.GetDateTime(8),
        PublishedAt   = r.IsDBNull(9)  ? null : r.GetDateTime(9),
        Error         = r.IsDBNull(10) ? null : r.GetString(10),
        RetryCount    = r.GetInt32(11),
        ScheduledFor  = r.IsDBNull(12) ? null : r.GetDateTime(12),
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(options.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
