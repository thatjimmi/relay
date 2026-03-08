using System.Data;
using Microsoft.Data.SqlClient;
using Relay.Inbox.Core;

namespace Relay.Inbox.Storage;

/// <summary>
/// SQL Server inbox store using raw ADO.NET (Microsoft.Data.SqlClient).
/// No ORM dependencies. Supports SKIP LOCKED for safe parallel processing.
/// </summary>
public sealed class SqlInboxStore(SqlInboxStoreOptions options) : IInboxStore
{
    private readonly string _table = options.TableName;

    // -------------------------------------------------------------------------
    // Schema
    // -------------------------------------------------------------------------

    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM sys.tables WHERE name = @table)
            BEGIN
                CREATE TABLE [{table}] (
                    Id              UNIQUEIDENTIFIER    NOT NULL PRIMARY KEY DEFAULT NEWSEQUENTIALID(),
                    InboxName       NVARCHAR(100)       NOT NULL,
                    [Type]          NVARCHAR(250)       NOT NULL,
                    IdempotencyKey  NVARCHAR(500)       NOT NULL,
                    Payload         NVARCHAR(MAX)       NOT NULL,
                    Status          TINYINT             NOT NULL DEFAULT 0,
                    ReceivedAt      DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
                    ProcessedAt     DATETIME2           NULL,
                    Error           NVARCHAR(2000)      NULL,
                    RetryCount      INT                 NOT NULL DEFAULT 0,
                    TraceId         NVARCHAR(100)       NULL,
                    Source          NVARCHAR(200)       NULL,

                    CONSTRAINT UQ_{table}_IdempotencyKey UNIQUE (IdempotencyKey)
                );

                CREATE NONCLUSTERED INDEX IX_{table}_InboxName_Status_ReceivedAt
                    ON [{table}] (InboxName, Status, ReceivedAt)
                    INCLUDE (Id, [Type], Payload);
            END

            IF NOT EXISTS (
                SELECT 1 FROM sys.columns
                WHERE object_id = OBJECT_ID(N'[{table}]') AND name = N'SourceTimestamp'
            )
            BEGIN
                ALTER TABLE [{table}] ADD SourceTimestamp DATETIME2 NULL;
            END
            """;

        // We can't use parameters for table names, but we control this value from config
        var safeSql = sql.Replace("{table}", _table);

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(safeSql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Write path
    // -------------------------------------------------------------------------

    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
    {
        var sql = $"SELECT 1 FROM [{_table}] WHERE IdempotencyKey = @key";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@key", idempotencyKey);

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is not null;
    }

    public async Task<(Guid Id, DateTime? SourceTimestamp)?> TryGetAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var sql = $"SELECT Id, SourceTimestamp FROM [{_table}] WHERE IdempotencyKey = @key";

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@key", SqlDbType.NVarChar, 500).Value = idempotencyKey;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;

        var id = reader.GetGuid(0);
        DateTime? sourceTimestamp = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
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
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@key",      SqlDbType.NVarChar, 500).Value  = idempotencyKey;
        cmd.Parameters.Add("@payload",  SqlDbType.NVarChar, -1).Value   = payload;
        cmd.Parameters.Add("@sourceTs", SqlDbType.DateTime2).Value      = sourceTimestamp;
        cmd.Parameters.Add("@now",      SqlDbType.DateTime2).Value      = DateTime.UtcNow;

        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task InsertAsync(InboxMessage message, CancellationToken ct = default)
    {
        var sql = $"""
            INSERT INTO [{_table}]
                (Id, InboxName, [Type], IdempotencyKey, Payload, Status, ReceivedAt, TraceId, Source, SourceTimestamp)
            VALUES
                (@id, @inboxName, @type, @key, @payload, @status, @receivedAt, @traceId, @source, @sourceTs)
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",         SqlDbType.UniqueIdentifier).Value = message.Id;
        cmd.Parameters.Add("@inboxName",  SqlDbType.NVarChar, 100).Value   = message.InboxName;
        cmd.Parameters.Add("@type",       SqlDbType.NVarChar, 250).Value   = message.Type;
        cmd.Parameters.Add("@key",        SqlDbType.NVarChar, 500).Value   = message.IdempotencyKey;
        cmd.Parameters.Add("@payload",    SqlDbType.NVarChar, -1).Value    = message.Payload;
        cmd.Parameters.Add("@status",     SqlDbType.TinyInt).Value         = (int)message.Status;
        cmd.Parameters.Add("@receivedAt", SqlDbType.DateTime2).Value       = message.ReceivedAt;
        cmd.Parameters.Add("@traceId",    SqlDbType.NVarChar, 100).Value   = (object?)message.TraceId ?? DBNull.Value;
        cmd.Parameters.Add("@source",     SqlDbType.NVarChar, 200).Value   = (object?)message.Source  ?? DBNull.Value;
        cmd.Parameters.Add("@sourceTs",   SqlDbType.DateTime2).Value       = (object?)message.SourceTimestamp ?? DBNull.Value;

        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // Read path
    // -------------------------------------------------------------------------

    public async Task<IReadOnlyList<InboxMessage>> GetPendingAsync(
        string inboxName, int batchSize, CancellationToken ct = default)
    {
        // SKIP LOCKED lets multiple processor instances run safely in parallel —
        // each one claims a distinct batch without distributed locking.
        var skipLocked = options.UseSkipLocked ? "WITH (UPDLOCK, READPAST)" : "WITH (UPDLOCK)";

        var sql = $"""
            SELECT TOP (@batch) Id, InboxName, [Type], IdempotencyKey, Payload,
                                Status, ReceivedAt, ProcessedAt, Error, RetryCount, TraceId, Source, SourceTimestamp
            FROM [{_table}] {skipLocked}
            WHERE InboxName = @inbox
              AND Status IN ({(int)InboxMessageStatus.Pending}, {(int)InboxMessageStatus.Failed})
            ORDER BY ReceivedAt ASC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@batch", SqlDbType.Int).Value        = batchSize;
        cmd.Parameters.Add("@inbox", SqlDbType.NVarChar, 100).Value = inboxName;

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
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",     SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value          = (int)InboxMessageStatus.Failed;
        cmd.Parameters.Add("@error",  SqlDbType.NVarChar, 2000).Value   = error;
        cmd.Parameters.Add("@retry",  SqlDbType.Int).Value              = retryCount;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE [{_table}]
            SET Status = @status, Error = @error
            WHERE Id = @id
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",     SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value          = (int)InboxMessageStatus.DeadLettered;
        cmd.Parameters.Add("@error",  SqlDbType.NVarChar, 2000).Value   = error;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // -------------------------------------------------------------------------
    // IInboxQuery — stats, dead-letters, purge
    // -------------------------------------------------------------------------

    public async Task<InboxStats> GetStatsAsync(string inboxName, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Status, COUNT(*) AS Cnt
            FROM [{_table}]
            WHERE InboxName = @inbox
            GROUP BY Status;

            SELECT MIN(ReceivedAt)
            FROM [{_table}]
            WHERE InboxName = @inbox AND Status = {(int)InboxMessageStatus.Pending};
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@inbox", SqlDbType.NVarChar, 100).Value = inboxName;

        var counts = new Dictionary<InboxMessageStatus, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
            counts[(InboxMessageStatus)reader.GetByte(0)] = reader.GetInt32(1);

        await reader.NextResultAsync(ct);
        DateTime? oldestPending = null;
        if (await reader.ReadAsync(ct) && !reader.IsDBNull(0))
            oldestPending = reader.GetDateTime(0);

        return new InboxStats(
            InboxName:     inboxName,
            Pending:       counts.GetValueOrDefault(InboxMessageStatus.Pending),
            Processing:    counts.GetValueOrDefault(InboxMessageStatus.Processing),
            Processed:     counts.GetValueOrDefault(InboxMessageStatus.Processed),
            Failed:        counts.GetValueOrDefault(InboxMessageStatus.Failed),
            DeadLettered:  counts.GetValueOrDefault(InboxMessageStatus.DeadLettered),
            OldestPending: oldestPending);
    }

    public async Task<IReadOnlyList<InboxMessage>> GetDeadLetteredAsync(
        string inboxName, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT TOP (@limit) Id, InboxName, [Type], IdempotencyKey, Payload,
                                Status, ReceivedAt, ProcessedAt, Error, RetryCount, TraceId, Source, SourceTimestamp
            FROM [{_table}]
            WHERE InboxName = @inbox AND Status = {(int)InboxMessageStatus.DeadLettered}
            ORDER BY ReceivedAt DESC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@inbox", SqlDbType.NVarChar, 100).Value = inboxName;
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value           = limit;
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<InboxMessage>> GetFailedAsync(
        string inboxName, int limit = 100, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT TOP (@limit) Id, InboxName, [Type], IdempotencyKey, Payload,
                                Status, ReceivedAt, ProcessedAt, Error, RetryCount, TraceId, Source, SourceTimestamp
            FROM [{_table}]
            WHERE InboxName = @inbox AND Status = {(int)InboxMessageStatus.Failed}
            ORDER BY ReceivedAt DESC
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@inbox", SqlDbType.NVarChar, 100).Value = inboxName;
        cmd.Parameters.Add("@limit", SqlDbType.Int).Value           = limit;
        return await ReadMessagesAsync(cmd, ct);
    }

    public async Task<int> PurgeProcessedAsync(string inboxName, TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var sql = $"""
            DELETE FROM [{_table}]
            WHERE InboxName = @inbox
              AND Status = {(int)InboxMessageStatus.Processed}
              AND ProcessedAt < @cutoff
            """;

        await using var conn = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@inbox",  SqlDbType.NVarChar, 100).Value = inboxName;
        cmd.Parameters.Add("@cutoff", SqlDbType.DateTime2).Value     = cutoff;
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
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = messageId;
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
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@inbox", SqlDbType.NVarChar, 100).Value = inboxName;
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
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.Add("@id",     SqlDbType.UniqueIdentifier).Value = id;
        cmd.Parameters.Add("@status", SqlDbType.TinyInt).Value          = (int)status;
        if (processedAt.HasValue)
            cmd.Parameters.Add("@ts", SqlDbType.DateTime2).Value = processedAt.Value;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<List<InboxMessage>> ReadMessagesAsync(SqlCommand cmd, CancellationToken ct)
    {
        var list = new List<InboxMessage>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            list.Add(MapRow(reader));
        return list;
    }

    private static InboxMessage MapRow(SqlDataReader r) => new()
    {
        Id              = r.GetGuid(0),
        InboxName       = r.GetString(1),
        Type            = r.GetString(2),
        IdempotencyKey  = r.GetString(3),
        Payload         = r.GetString(4),
        Status          = (InboxMessageStatus)r.GetByte(5),
        ReceivedAt      = r.GetDateTime(6),
        ProcessedAt     = r.IsDBNull(7) ? null : r.GetDateTime(7),
        Error           = r.IsDBNull(8) ? null : r.GetString(8),
        RetryCount      = r.GetInt32(9),
        TraceId         = r.IsDBNull(10) ? null : r.GetString(10),
        Source          = r.IsDBNull(11) ? null : r.GetString(11),
        SourceTimestamp = r.IsDBNull(12) ? null : r.GetDateTime(12),
    };

    private async Task<SqlConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqlConnection(options.ConnectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
