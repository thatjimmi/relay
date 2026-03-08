namespace Relay.Inbox.Core;

/// <summary>
/// Implement one of these per message type. Defines both how to derive
/// uniqueness from data and how to handle the message once it's processed.
/// </summary>
public interface IInboxHandler<TMessage>
{
    /// <summary>
    /// Derive a stable idempotency key purely from the message data.
    /// Same logical message must always return the same key.
    /// Example: $"trade:{msg.Exchange}:{msg.ExternalTradeId}"
    /// </summary>
    string GetIdempotencyKey(TMessage message);

    Task HandleAsync(TMessage message, CancellationToken ct = default);
}

/// <summary>
/// Entry point — receive a message and store it safely. Inject this
/// at your API/WebSocket/consumer boundary.
/// </summary>
public interface IInboxReceiver<TMessage>
{
    Task<InboxReceiveResult> ReceiveAsync(TMessage message, CancellationToken ct = default);

    /// <summary>
    /// Override the source tag for this call (e.g. "binance-ws", "stripe-webhook").
    /// </summary>
    Task<InboxReceiveResult> ReceiveAsync(TMessage message, string source, CancellationToken ct = default);

    /// <summary>
    /// Receive with a source timestamp from the external system. If a message with the
    /// same idempotency key already exists but with an older (or absent) source timestamp,
    /// the payload is updated and the message is re-queued for processing.
    /// </summary>
    Task<InboxReceiveResult> ReceiveAsync(TMessage message, DateTime sourceTimestamp, CancellationToken ct = default);

    /// <summary>
    /// Receive with both a source tag and a source timestamp.
    /// </summary>
    Task<InboxReceiveResult> ReceiveAsync(TMessage message, string source, DateTime sourceTimestamp, CancellationToken ct = default);
}

/// <summary>
/// Process pending messages for a named inbox. Call this from
/// wherever makes sense: a Hangfire job, a minimal API endpoint,
/// a triggered Azure Function, a test.
/// </summary>
public interface IInboxProcessor
{
    Task<InboxProcessResult> ProcessPendingAsync(
        string inboxName,
        int batchSize = 50,
        CancellationToken ct = default);
}

/// <summary>
/// Query inbox state — useful for dashboards, health checks, dead-letter inspection.
/// </summary>
public interface IInboxQuery
{
    Task<InboxStats> GetStatsAsync(string inboxName, CancellationToken ct = default);
    Task<IReadOnlyList<InboxMessage>> GetDeadLetteredAsync(string inboxName, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<InboxMessage>> GetFailedAsync(string inboxName, int limit = 100, CancellationToken ct = default);
    Task<int> PurgeProcessedAsync(string inboxName, TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>
/// Requeue a dead-lettered or failed message for reprocessing.
/// </summary>
public interface IInboxRequeue
{
    Task<bool> RequeueAsync(Guid messageId, CancellationToken ct = default);
    Task<int> RequeueAllDeadLetteredAsync(string inboxName, CancellationToken ct = default);
}

/// <summary>
/// Low-level storage abstraction — swap freely for EF, Mongo, Redis, etc.
/// </summary>
public interface IInboxStore : IInboxQuery, IInboxRequeue
{
    Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Returns the existing message's Id and SourceTimestamp if the key exists, or null if not found.
    /// Used to support source-timestamp-based update semantics.
    /// </summary>
    Task<(Guid Id, DateTime? SourceTimestamp)?> TryGetAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// Updates payload + source timestamp for an existing key and resets status to Pending,
    /// but only if the stored SourceTimestamp is null or older than <paramref name="sourceTimestamp"/>.
    /// Returns true if the update was applied.
    /// </summary>
    Task<bool> UpdateIfNewerAsync(string idempotencyKey, string payload, DateTime sourceTimestamp, CancellationToken ct = default);

    Task InsertAsync(InboxMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<InboxMessage>> GetPendingAsync(string inboxName, int batchSize, CancellationToken ct = default);
    Task MarkProcessingAsync(Guid id, CancellationToken ct = default);
    Task MarkProcessedAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string error, int retryCount, CancellationToken ct = default);
    Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default);
    Task EnsureSchemaAsync(CancellationToken ct = default);
}
