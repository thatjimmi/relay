using System.Collections.Concurrent;
using Relay.Inbox.Core;

namespace Relay.Inbox.Storage;

/// <summary>
/// In-memory store for unit/integration tests. Ships as part of the main package
/// so there's zero extra dependencies needed in test projects.
/// Thread-safe. Not for production use.
/// </summary>
public sealed class InMemoryInboxStore : IInboxStore
{
    private readonly ConcurrentDictionary<string, InboxMessage> _byKey = new();
    private readonly ConcurrentDictionary<Guid, InboxMessage>   _byId  = new();

    // -------------------------------------------------------------------------
    // Write path
    // -------------------------------------------------------------------------

    public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default) =>
        Task.FromResult(_byKey.ContainsKey(idempotencyKey));

    public Task<(Guid Id, DateTime? SourceTimestamp)?> TryGetAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        if (_byKey.TryGetValue(idempotencyKey, out var m))
            return Task.FromResult<(Guid, DateTime?)?>(( m.Id, m.SourceTimestamp ));
        return Task.FromResult<(Guid, DateTime?)?>(null);
    }

    public Task<bool> UpdateIfNewerAsync(
        string idempotencyKey, string payload, DateTime sourceTimestamp, CancellationToken ct = default)
    {
        if (!_byKey.TryGetValue(idempotencyKey, out var m))
            return Task.FromResult(false);

        if (m.SourceTimestamp.HasValue && sourceTimestamp <= m.SourceTimestamp.Value)
            return Task.FromResult(false);

        m.Payload         = payload;
        m.SourceTimestamp = sourceTimestamp;
        m.Status          = InboxMessageStatus.Pending;
        m.ReceivedAt      = DateTime.UtcNow;
        m.Error           = null;
        m.RetryCount      = 0;
        m.ProcessedAt     = null;
        return Task.FromResult(true);
    }

    public Task InsertAsync(InboxMessage message, CancellationToken ct = default)
    {
        _byId[message.Id] = message;
        if (message.IdempotencyKey is not null)
            _byKey[message.IdempotencyKey] = message;
        return Task.CompletedTask;
    }

    public Task<bool> SetIdempotencyKeyAsync(Guid id, string idempotencyKey, CancellationToken ct = default)
    {
        if (_byKey.ContainsKey(idempotencyKey))
            return Task.FromResult(false);

        if (!_byId.TryGetValue(id, out var m))
            return Task.FromResult(false);

        m.IdempotencyKey = idempotencyKey;
        _byKey[idempotencyKey] = m;
        return Task.FromResult(true);
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        if (_byId.TryRemove(id, out var m) && m.IdempotencyKey is not null)
            _byKey.TryRemove(m.IdempotencyKey, out _);
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // Read path
    // -------------------------------------------------------------------------

    public Task<IReadOnlyList<InboxMessage>> GetPendingAsync(
        string inboxName, int batchSize, CancellationToken ct = default)
    {
        var results = _byId.Values
            .Where(m => m.InboxName == inboxName &&
                        m.Status is InboxMessageStatus.Pending or InboxMessageStatus.Failed)
            .OrderBy(m => m.ReceivedAt)
            .Take(batchSize)
            .ToList();

        return Task.FromResult<IReadOnlyList<InboxMessage>>(results);
    }

    // -------------------------------------------------------------------------
    // Status transitions
    // -------------------------------------------------------------------------

    public Task MarkProcessingAsync(Guid id, CancellationToken ct = default) =>
        SetStatus(id, InboxMessageStatus.Processing);

    public Task MarkProcessedAsync(Guid id, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var m))
        {
            m.Status = InboxMessageStatus.Processed;
            m.ProcessedAt = DateTime.UtcNow;
        }
        return Task.CompletedTask;
    }

    public Task MarkFailedAsync(Guid id, string error, int retryCount, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var m))
        {
            m.Status = InboxMessageStatus.Failed;
            m.Error = error;
            m.RetryCount = retryCount;
        }
        return Task.CompletedTask;
    }

    public Task MarkDeadLetteredAsync(Guid id, string error, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(id, out var m))
        {
            m.Status = InboxMessageStatus.DeadLettered;
            m.Error = error;
        }
        return Task.CompletedTask;
    }

    // -------------------------------------------------------------------------
    // IInboxQuery
    // -------------------------------------------------------------------------

    public Task<InboxStats> GetStatsAsync(string inboxName, CancellationToken ct = default)
    {
        var msgs = _byId.Values.Where(m => m.InboxName == inboxName).ToList();
        var oldest = msgs
            .Where(m => m.Status == InboxMessageStatus.Pending)
            .OrderBy(m => m.ReceivedAt)
            .FirstOrDefault()?.ReceivedAt;

        return Task.FromResult(new InboxStats(
            InboxName:     inboxName,
            Pending:       msgs.Count(m => m.Status == InboxMessageStatus.Pending),
            Processing:    msgs.Count(m => m.Status == InboxMessageStatus.Processing),
            Processed:     msgs.Count(m => m.Status == InboxMessageStatus.Processed),
            Failed:        msgs.Count(m => m.Status == InboxMessageStatus.Failed),
            DeadLettered:  msgs.Count(m => m.Status == InboxMessageStatus.DeadLettered),
            OldestPending: oldest));
    }

    public Task<IReadOnlyList<InboxMessage>> GetDeadLetteredAsync(
        string inboxName, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InboxMessage>>(
            _byId.Values
                 .Where(m => m.InboxName == inboxName && m.Status == InboxMessageStatus.DeadLettered)
                 .OrderByDescending(m => m.ReceivedAt)
                 .Take(limit)
                 .ToList());

    public Task<IReadOnlyList<InboxMessage>> GetFailedAsync(
        string inboxName, int limit = 100, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InboxMessage>>(
            _byId.Values
                 .Where(m => m.InboxName == inboxName && m.Status == InboxMessageStatus.Failed)
                 .OrderByDescending(m => m.ReceivedAt)
                 .Take(limit)
                 .ToList());

    public Task<int> PurgeProcessedAsync(string inboxName, TimeSpan olderThan, CancellationToken ct = default)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var toRemove = _byId.Values
            .Where(m => m.InboxName == inboxName &&
                        m.Status == InboxMessageStatus.Processed &&
                        m.ProcessedAt < cutoff)
            .ToList();

        foreach (var m in toRemove)
        {
            _byId.TryRemove(m.Id, out _);
            if (m.IdempotencyKey is not null)
                _byKey.TryRemove(m.IdempotencyKey, out _);
        }

        return Task.FromResult(toRemove.Count);
    }

    // -------------------------------------------------------------------------
    // IInboxRequeue
    // -------------------------------------------------------------------------

    public Task<bool> RequeueAsync(Guid messageId, CancellationToken ct = default)
    {
        if (_byId.TryGetValue(messageId, out var m) &&
            m.Status is InboxMessageStatus.DeadLettered or InboxMessageStatus.Failed)
        {
            m.Status = InboxMessageStatus.Pending;
            m.Error = null;
            m.RetryCount = 0;
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<int> RequeueAllDeadLetteredAsync(string inboxName, CancellationToken ct = default)
    {
        var msgs = _byId.Values
            .Where(m => m.InboxName == inboxName && m.Status == InboxMessageStatus.DeadLettered)
            .ToList();

        foreach (var m in msgs)
        {
            m.Status = InboxMessageStatus.Pending;
            m.Error = null;
            m.RetryCount = 0;
        }

        return Task.FromResult(msgs.Count);
    }

    public Task EnsureSchemaAsync(CancellationToken ct = default) => Task.CompletedTask;

    // -------------------------------------------------------------------------
    // Test helpers
    // -------------------------------------------------------------------------

    /// <summary>All stored messages — useful for assertions in tests.</summary>
    public IReadOnlyList<InboxMessage> All => _byId.Values.ToList();

    /// <summary>Reset all state between tests.</summary>
    public void Clear() { _byKey.Clear(); _byId.Clear(); }

    // -------------------------------------------------------------------------

    private Task SetStatus(Guid id, InboxMessageStatus status)
    {
        if (_byId.TryGetValue(id, out var m)) m.Status = status;
        return Task.CompletedTask;
    }
}
