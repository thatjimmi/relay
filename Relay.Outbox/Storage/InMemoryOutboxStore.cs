using System.Collections.Concurrent;
using Relay.Outbox.Core;

namespace Relay.Outbox.Storage;

/// <summary>
/// In-memory outbox store for unit/integration tests.
/// Thread-safe. Ships in the main package — zero extra dependencies.
/// Not for production use.
/// </summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly ConcurrentDictionary<Guid, OutboxMessage> _byId = new();

    // -------------------------------------------------------------------------
    // Write path
    // -------------------------------------------------------------------------

    public Task InsertAsync(OutboxMessage message, CancellationToken ct = default)
    {
        _byId[message.Id] = message;
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Read path
    // -------------------------------------------------------------------------

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
        string outboxName, int batchSize, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var results = _byId.Values
            .Where(m => m.OutboxName == outboxName &&
                        m.Status is OutboxMessageStatus.Pending or OutboxMessageStatus.Failed &&
                        (m.ScheduledFor is null || m.ScheduledFor <= now))
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToList();

        return Task.FromResult<IReadOnlyList<OutboxMessage>>(results);
    }

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    public Task MarkDispatchingAsync(Guid id, CancellationToken ct = default) =>
        SetStatus(id, OutboxMessageStatus.Dispatching);

    public Task MarkPublishedAsync(Guid id, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var m))
        {
            m.Status = OutboxMessageStatus.Published;
            m.PublishedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid id, string error, int retryCount, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var m))
        {
            m.Status = OutboxMessageStatus.Failed;
            m.Error = error;
            m.RetryCount = retryCount;
        }
        return Task.CompletedTask;
    }

    public Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var m))
        {
            m.Status = OutboxMessageStatus.DeadLettered;
            m.Error = error;
        }
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // IOutboxQuery
    // -------------------------------------------------------------------------

    public Task<OutboxStats> GetStatsAsync(string outboxName, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var msgs = _byId.Values.Where(m => m.OutboxName == outboxName).ToList();
        var oldest = msgs
            .Where(m => m.Status == OutboxMessageStatus.Pending &&
                        (m.ScheduledFor is null || m.ScheduledFor <= now))
            .OrderBy(m => m.CreatedAt)
            .FirstOrDefault()?.CreatedAt;

        return Task.FromResult(new OutboxStats(
            OutboxName:    outboxName,
            Pending:       msgs.Count(m => m.Status == OutboxMessageStatus.Pending),
            Dispatching:   msgs.Count(m => m.Status == OutboxMessageStatus.Dispatching),
            Published:     msgs.Count(m => m.Status == OutboxMessageStatus.Published),
            Failed:        msgs.Count(m => m.Status == OutboxMessageStatus.Failed),
            DeadLettered:  msgs.Count(m => m.Status == OutboxMessageStatus.DeadLettered),
            Scheduled:     msgs.Count(m => m.Status == OutboxMessageStatus.Pending &&
                                           m.ScheduledFor > now),
            OldestPending: oldest));
    }

    public Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(
        string outboxName, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(
            _byId.Values
                 .Where(m => m.OutboxName == outboxName &&
                             m.Status == OutboxMessageStatus.DeadLettered)
                 .OrderByDescending(m => m.CreatedAt)
                 .Take(limit).ToList());

    public Task<IReadOnlyList<OutboxMessage>> GetFailedAsync(
        string outboxName, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(
            _byId.Values
                 .Where(m => m.OutboxName == outboxName &&
                             m.Status == OutboxMessageStatus.Failed)
                 .OrderByDescending(m => m.CreatedAt)
                 .Take(limit).ToList());

    public Task<IReadOnlyList<OutboxMessage>> GetByCorrelationIdAsync(
        string correlationId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<OutboxMessage>>(
            _byId.Values
                 .Where(m => m.CorrelationId == correlationId)
                 .OrderBy(m => m.CreatedAt).ToList());

    public Task<int> PurgePublishedAsync(
        string outboxName, TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var toRemove = _byId.Values
            .Where(m => m.OutboxName == outboxName &&
                        m.Status == OutboxMessageStatus.Published &&
                        m.PublishedAt < cutoff)
            .ToList();

        foreach (var m in toRemove) _byId.TryRemove(m.Id, out _);
        return Task.FromResult(toRemove.Count);
    }

    // -------------------------------------------------------------------------
    // IOutboxRequeue
    // -------------------------------------------------------------------------

    public Task<bool> RequeueAsync(Guid messageId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(messageId, out var m) &&
            m.Status is OutboxMessageStatus.DeadLettered or OutboxMessageStatus.Failed)
        {
            m.Status = OutboxMessageStatus.Pending;
            m.Error = null;
            m.RetryCount = 0;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<int> RequeueAllDeadLetteredAsync(
        string outboxName, CancellationToken ct = default)
    {
        var msgs = _byId.Values
            .Where(m => m.OutboxName == outboxName &&
                        m.Status == OutboxMessageStatus.DeadLettered).ToList();

        foreach (var m in msgs) { m.Status = OutboxMessageStatus.Pending; m.Error = null; m.RetryCount = 0; }
        return Task.FromResult(msgs.Count);
    }

    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    /// <summary>All stored messages — for assertions in tests.</summary>
    public IReadOnlyList<OutboxMessage> All => _byId.Values.ToList();

    /// <summary>Reset all state between tests.</summary>
    public void Clear() => _byId.Clear();

    private Task SetStatus(Guid id, OutboxMessageStatus status)
    {
        if (_byId.TryGetValue(id, out var m)) m.Status = status;
        return Task.CompletedTask;
    }
}
