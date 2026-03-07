namespace Relay.Outbox.Core;

/// <summary>
/// Implement one per message type. Defines how to publish a message
/// to the outside world — RabbitMQ, Azure Service Bus, HTTP, stdout, anything.
/// </summary>
public interface IOutboxPublisher<TMessage>
{
    /// <summary>
    /// Publish the message to the external transport.
    /// Must be idempotent where possible — the dispatcher may call this
    /// more than once if a previous attempt crashed after publishing but
    /// before marking as Published.
    /// </summary>
    Task PublishAsync(TMessage message, OutboxMessage envelope, CancellationToken ct = default);
}

/// <summary>
/// Write a message to the outbox. Inject this inside your inbox handler
/// (or anywhere in your domain) to stage an outgoing message atomically
/// with your business logic.
/// </summary>
public interface IOutboxWriter
{
    /// <summary>
    /// Stage a message for eventual dispatch.
    /// The message is persisted immediately — dispatch happens separately via IOutboxDispatcher.
    /// </summary>
    Task<OutboxWriteResult> WriteAsync<TMessage>(
        TMessage message,
        string outboxName,
        CancellationToken ct = default);

    /// <summary>
    /// Stage a message with an explicit destination hint (topic, queue, exchange name).
    /// </summary>
    Task<OutboxWriteResult> WriteAsync<TMessage>(
        TMessage message,
        string outboxName,
        string destination,
        CancellationToken ct = default);

    /// <summary>
    /// Stage a message for deferred dispatch — will not be sent before scheduledFor.
    /// </summary>
    Task<OutboxWriteResult> WriteScheduledAsync<TMessage>(
        TMessage message,
        string outboxName,
        DateTime scheduledFor,
        CancellationToken ct = default);
}

/// <summary>
/// Dispatch pending outbox messages to their publishers.
/// Call this from wherever fits your stack — Hangfire, Azure Function, minimal API.
/// No background threads baked in.
/// </summary>
public interface IOutboxDispatcher
{
    Task<OutboxDispatchResult> DispatchPendingAsync(
        string outboxName,
        int batchSize = 50,
        CancellationToken ct = default);
}

/// <summary>
/// Query outbox state — stats, dead letters, failures.
/// </summary>
public interface IOutboxQuery
{
    Task<OutboxStats> GetStatsAsync(string outboxName, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(string outboxName, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetFailedAsync(string outboxName, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Find all outbox messages correlated to a given inbox message ID.
    /// Useful for tracing cause → effect across the inbox/outbox boundary.
    /// </summary>
    Task<IReadOnlyList<OutboxMessage>> GetByCorrelationIdAsync(string correlationId, CancellationToken ct = default);

    Task<int> PurgePublishedAsync(string outboxName, TimeSpan olderThan, CancellationToken ct = default);
}

/// <summary>
/// Requeue failed or dead-lettered outbox messages for re-dispatch.
/// </summary>
public interface IOutboxRequeue
{
    Task<bool> RequeueAsync(Guid messageId, CancellationToken ct = default);
    Task<int> RequeueAllDeadLetteredAsync(string outboxName, CancellationToken ct = default);
}

/// <summary>
/// Full storage abstraction. Swap for EF, Mongo, Redis — anything.
/// </summary>
public interface IOutboxStore : IOutboxQuery, IOutboxRequeue
{
    Task InsertAsync(OutboxMessage message, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(string outboxName, int batchSize, CancellationToken ct = default);
    Task MarkDispatchingAsync(Guid id, CancellationToken ct = default);
    Task MarkPublishedAsync(Guid id, CancellationToken ct = default);
    Task MarkFailedAsync(Guid id, string error, int retryCount, CancellationToken ct = default);
    Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default);
    Task EnsureSchemaAsync(CancellationToken ct = default);
}
