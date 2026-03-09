using System.Text.Json;
using Relay.Inbox.Core;

namespace Relay.Inbox.Internal;

internal sealed class InboxReceiver<TMessage>(
    IInboxStore store,
    IInboxHandler<TMessage> handler,
    string inboxName,
    InboxOptions options) : IInboxReceiver<TMessage>
{
    public Task<InboxReceiveResult> ReceiveAsync(TMessage message, CancellationToken ct = default) =>
        ReceiveAsync(message, source: null, sourceTimestamp: null, ct);

    public Task<InboxReceiveResult> ReceiveAsync(TMessage message, string? source, CancellationToken ct = default) =>
        ReceiveAsync(message, source, sourceTimestamp: null, ct);

    public Task<InboxReceiveResult> ReceiveAsync(TMessage message, DateTime sourceTimestamp, CancellationToken ct = default) =>
        ReceiveAsync(message, source: null, sourceTimestamp, ct);

    public Task<InboxReceiveResult> ReceiveAsync(TMessage message, string? source, DateTime sourceTimestamp, CancellationToken ct = default) =>
        ReceiveAsync(message, source, (DateTime?)sourceTimestamp, ct);

    private async Task<InboxReceiveResult> ReceiveAsync(
        TMessage message, string? source, DateTime? sourceTimestamp, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var payload = JsonSerializer.Serialize(message);

        // Step 1: persist the raw payload immediately — safe regardless of key derivation.
        var inboxMessage = new InboxMessage
        {
            InboxName       = inboxName,
            Type            = typeof(TMessage).Name,
            Payload         = payload,
            SourceTimestamp = sourceTimestamp,
            Source          = source,
            TraceId         = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        };

        await store.InsertAsync(inboxMessage, ct);

        // Step 2: derive the idempotency key and deduplicate.
        var rawKey    = handler.GetIdempotencyKey(message);
        var scopedKey = $"{inboxName.ToLowerInvariant()}:{rawKey}";

        if (sourceTimestamp.HasValue)
        {
            var existing = await store.TryGetAsync(scopedKey, ct);
            if (existing.HasValue)
            {
                var (existingId, existingTs) = existing.Value;

                if (existingTs.HasValue && sourceTimestamp.Value <= existingTs.Value)
                {
                    // Incoming is same age or older — duplicate; discard the orphan we stored.
                    await store.DeleteAsync(inboxMessage.Id, ct);

                    if (options.OnDuplicate is not null)
                        await options.OnDuplicate(scopedKey);

                    return InboxReceiveResult.Duplicate();
                }

                // Incoming is newer — update the existing record, discard the orphan.
                var updated = await store.UpdateIfNewerAsync(scopedKey, payload, sourceTimestamp.Value, ct);
                await store.DeleteAsync(inboxMessage.Id, ct);

                if (!updated)
                    return InboxReceiveResult.Duplicate();

                var updatedMessage = new InboxMessage
                {
                    Id              = existingId,
                    InboxName       = inboxName,
                    Type            = typeof(TMessage).Name,
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

            // No existing record with this key — claim it.
            await store.SetIdempotencyKeyAsync(inboxMessage.Id, scopedKey, ct);
        }
        else
        {
            // Simple deduplication: try to claim the key atomically.
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
}
