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

        var rawKey = handler.GetIdempotencyKey(message);
        var scopedKey = $"{inboxName.ToLowerInvariant()}:{rawKey}";
        var payload = JsonSerializer.Serialize(message);

        if (sourceTimestamp.HasValue)
        {
            var existing = await store.TryGetAsync(scopedKey, ct);
            if (existing.HasValue)
            {
                var (existingId, existingTs) = existing.Value;

                if (existingTs.HasValue && sourceTimestamp.Value <= existingTs.Value)
                {
                    // Incoming is same age or older — true duplicate
                    if (options.OnDuplicate is not null)
                        await options.OnDuplicate(scopedKey);

                    return InboxReceiveResult.Duplicate();
                }

                // Incoming is newer — update payload and re-queue
                var updated = await store.UpdateIfNewerAsync(scopedKey, payload, sourceTimestamp.Value, ct);
                if (!updated)
                {
                    // Lost the race to a concurrent update with a newer timestamp
                    return InboxReceiveResult.Duplicate();
                }

                var updatedMessage = new InboxMessage
                {
                    Id             = existingId,
                    InboxName      = inboxName,
                    Type           = typeof(TMessage).Name,
                    IdempotencyKey = scopedKey,
                    Payload        = payload,
                    SourceTimestamp = sourceTimestamp,
                    Source         = source,
                    TraceId        = System.Diagnostics.Activity.Current?.TraceId.ToString(),
                };

                if (options.OnMessageUpdated is not null)
                    await options.OnMessageUpdated(updatedMessage);

                return InboxReceiveResult.Updated(existingId);
            }

            // Key doesn't exist yet — fall through to normal insert
        }
        else
        {
            if (await store.ExistsAsync(scopedKey, ct))
            {
                if (options.OnDuplicate is not null)
                    await options.OnDuplicate(scopedKey);

                return InboxReceiveResult.Duplicate();
            }
        }

        var inboxMessage = new InboxMessage
        {
            InboxName       = inboxName,
            Type            = typeof(TMessage).Name,
            IdempotencyKey  = scopedKey,
            Payload         = payload,
            SourceTimestamp = sourceTimestamp,
            Source          = source,
            TraceId         = System.Diagnostics.Activity.Current?.TraceId.ToString(),
        };

        await store.InsertAsync(inboxMessage, ct);

        if (options.OnMessageStored is not null)
            await options.OnMessageStored(inboxMessage);

        return InboxReceiveResult.Stored(inboxMessage.Id);
    }
}
