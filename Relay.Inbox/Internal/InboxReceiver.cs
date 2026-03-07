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
        ReceiveAsync(message, source: null, ct);

    public async Task<InboxReceiveResult> ReceiveAsync(TMessage message, string? source, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var rawKey = handler.GetIdempotencyKey(message);
        var scopedKey = $"{inboxName.ToLowerInvariant()}:{rawKey}";

        if (await store.ExistsAsync(scopedKey, ct))
        {
            if (options.OnDuplicate is not null)
                await options.OnDuplicate(scopedKey);

            return InboxReceiveResult.Duplicate();
        }

        var inboxMessage = new InboxMessage
        {
            InboxName = inboxName,
            Type = typeof(TMessage).Name,
            IdempotencyKey = scopedKey,
            Payload = JsonSerializer.Serialize(message),
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            Source = source
        };

        await store.InsertAsync(inboxMessage, ct);
        return InboxReceiveResult.Stored(inboxMessage.Id);
    }
}
