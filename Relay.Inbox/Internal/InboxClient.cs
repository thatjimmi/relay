using System.Text.Json;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;

namespace Relay.Inbox.Internal;

internal sealed class InboxClient(
    InboxStoreResolver storeResolver,
    InboxOptions options,
    ILogger<InboxClient> logger) : IInboxClient
{
    // ── Receive ───────────────────────────────────────────────────────────────

    public Task<InboxReceiveResult> ReceiveAsync(
        string inboxName, object message, string idempotencyKey, CancellationToken ct = default) =>
        ReceiveAsync(inboxName, message, idempotencyKey, source: null, sourceTimestamp: null, ct);

    public Task<InboxReceiveResult> ReceiveAsync(
        string inboxName, object message, string idempotencyKey, string source, CancellationToken ct = default) =>
        ReceiveAsync(inboxName, message, idempotencyKey, source, sourceTimestamp: null, ct);

    public Task<InboxReceiveResult> ReceiveAsync(
        string inboxName, object message, string idempotencyKey, DateTime sourceTimestamp, CancellationToken ct = default) =>
        ReceiveAsync(inboxName, message, idempotencyKey, source: null, (DateTime?)sourceTimestamp, ct);

    public Task<InboxReceiveResult> ReceiveAsync(
        string inboxName, object message, string idempotencyKey, string source, DateTime sourceTimestamp, CancellationToken ct = default) =>
        ReceiveAsync(inboxName, message, idempotencyKey, source, (DateTime?)sourceTimestamp, ct);

    private async Task<InboxReceiveResult> ReceiveAsync(
        string inboxName, object message, string idempotencyKey, string? source, DateTime? sourceTimestamp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var store = storeResolver.Resolve(inboxName);
        var payload = JsonSerializer.Serialize(message);

        // Step 1: persist the raw payload immediately — safe regardless of key scoping.
        var inboxMessage = new InboxMessage
        {
            InboxName       = inboxName,
            Type            = message.GetType().Name,
            Payload         = payload,
            SourceTimestamp = sourceTimestamp,
            Source          = source,
            TraceId         = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        };

        await store.InsertAsync(inboxMessage, ct);

        // Step 2: scope the key to this inbox and deduplicate.
        var scopedKey = $"{inboxName.ToLowerInvariant()}:{idempotencyKey}";

        if (sourceTimestamp.HasValue)
        {
            var existing = await store.TryGetAsync(scopedKey, ct);
            if (existing.HasValue)
            {
                var (existingId, existingTs) = existing.Value;

                if (existingTs.HasValue && sourceTimestamp.Value <= existingTs.Value)
                {
                    await store.DeleteAsync(inboxMessage.Id, ct);

                    if (options.OnDuplicate is not null)
                        await options.OnDuplicate(scopedKey);

                    return InboxReceiveResult.Duplicate();
                }

                var updated = await store.UpdateIfNewerAsync(scopedKey, payload, sourceTimestamp.Value, ct);
                await store.DeleteAsync(inboxMessage.Id, ct);

                if (!updated)
                    return InboxReceiveResult.Duplicate();

                var updatedMessage = new InboxMessage
                {
                    Id              = existingId,
                    InboxName       = inboxName,
                    Type            = message.GetType().Name,
                    IdempotencyKey  = scopedKey,
                    Payload         = payload,
                    SourceTimestamp = sourceTimestamp,
                    Source          = source,
                    TraceId         = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                };

                if (options.OnMessageUpdated is not null)
                    await options.OnMessageUpdated(updatedMessage);

                return InboxReceiveResult.Updated(existingId);
            }

            await store.SetIdempotencyKeyAsync(inboxMessage.Id, scopedKey, ct);
        }
        else
        {
            var isNew = await store.SetIdempotencyKeyAsync(inboxMessage.Id, scopedKey, ct);
            if (!isNew)
            {
                await store.DeleteAsync(inboxMessage.Id, ct);

                if (options.OnDuplicate is not null)
                    await options.OnDuplicate(scopedKey);

                return InboxReceiveResult.Duplicate();
            }
        }

        if (options.OnMessageStored is not null)
            await options.OnMessageStored(inboxMessage);

        return InboxReceiveResult.Stored(inboxMessage.Id);
    }

    // ── Process ───────────────────────────────────────────────────────────────

    public Task<IReadOnlyList<InboxMessage>> GetPendingAsync(
        string inboxName, int batchSize = 50, CancellationToken ct = default)
    {
        var store = storeResolver.Resolve(inboxName);
        return store.GetPendingAsync(inboxName, batchSize, ct);
    }

    public async Task MarkProcessedAsync(InboxMessage message, CancellationToken ct = default)
    {
        var store = storeResolver.Resolve(message.InboxName);
        message.ProcessedAt = DateTime.UtcNow;
        await store.MarkProcessedAsync(message.Id, ct);

        if (options.OnProcessed is not null)
            await options.OnProcessed(message);
    }

    public async Task MarkFailedAsync(
        InboxMessage message, string error, int maxRetries = 5, CancellationToken ct = default)
    {
        var store = storeResolver.Resolve(message.InboxName);
        var nextRetry = message.RetryCount + 1;
        var willDeadLetter = nextRetry >= maxRetries;

        logger.LogWarning(
            "Failed inbox message {Id} [{Type}] attempt {Attempt}/{Max} in {Inbox}",
            message.Id, message.Type, nextRetry, maxRetries, message.InboxName);

        if (options.OnFailed is not null)
            await options.OnFailed(message, new Exception(error));

        if (willDeadLetter)
        {
            await store.MarkDeadLetteredAsync(message.Id, error, ct);

            if (options.OnDeadLettered is not null)
                await options.OnDeadLettered(message, new Exception(error));

            logger.LogError(
                "Dead-lettered inbox message {Id} [{Type}] in {Inbox} after {Max} attempts",
                message.Id, message.Type, message.InboxName, maxRetries);
        }
        else
        {
            await store.MarkFailedAsync(message.Id, error, nextRetry, ct);
        }
    }
}
