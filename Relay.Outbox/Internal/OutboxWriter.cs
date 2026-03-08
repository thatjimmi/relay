using System.Text.Json;
using Relay.Outbox.Core;

namespace Relay.Outbox.Internal;

internal sealed class OutboxWriter(OutboxStoreResolver storeResolver, OutboxOptionsResolver optionsResolver) : IOutboxWriter
{
    public Task<OutboxWriteResult> WriteAsync<TMessage>(
        TMessage message,
        string outboxName,
        CancellationToken ct = default) =>
        WriteInternalAsync(message, outboxName, destination: null, scheduledFor: null, ct);

    public Task<OutboxWriteResult> WriteAsync<TMessage>(
        TMessage message,
        string outboxName,
        string destination,
        CancellationToken ct = default) =>
        WriteInternalAsync(message, outboxName, destination, scheduledFor: null, ct);

    public Task<OutboxWriteResult> WriteScheduledAsync<TMessage>(
        TMessage message,
        string outboxName,
        DateTime scheduledFor,
        CancellationToken ct = default) =>
        WriteInternalAsync(message, outboxName, destination: null, scheduledFor, ct);

    private async Task<OutboxWriteResult> WriteInternalAsync<TMessage>(
        TMessage message,
        string outboxName,
        string? destination,
        DateTime? scheduledFor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var store = storeResolver.Resolve(outboxName);

        var outboxMessage = new OutboxMessage
        {
            OutboxName = outboxName,
            Type = typeof(TMessage).Name,
            Payload = JsonSerializer.Serialize(message),
            Destination = destination,
            ScheduledFor = scheduledFor,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            // Capture correlation from ambient context if set by inbox processor
            CorrelationId = OutboxCorrelationContext.Current,
        };

        await store.InsertAsync(outboxMessage, ct);

        var opts = optionsResolver.TryResolve(outboxName);
        if (opts?.OnMessageStored is not null)
            await opts.OnMessageStored(outboxMessage);

        return OutboxWriteResult.Written(outboxMessage.Id, scheduledFor.HasValue);
    }
}
