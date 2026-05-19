using System.Data.Common;
using System.Text.Json;
using Relay.Outbox.Core;

namespace Relay.Outbox.Internal;

internal sealed class TransactionalOutboxWriter(OutboxStoreResolver storeResolver, OutboxOptionsResolver optionsResolver) : ITransactionalOutboxWriter
{
    public Task<OutboxWriteResult> WriteAsync<TMessage>(
        TMessage message,
        string outboxName,
        DbTransaction transaction,
        CancellationToken ct = default) =>
        WriteInternalAsync(message, outboxName, destination: null, transaction, ct);

    public Task<OutboxWriteResult> WriteAsync<TMessage>(
        TMessage message,
        string outboxName,
        string destination,
        DbTransaction transaction,
        CancellationToken ct = default) =>
        WriteInternalAsync(message, outboxName, destination, transaction, ct);

    private async Task<OutboxWriteResult> WriteInternalAsync<TMessage>(
        TMessage message,
        string outboxName,
        string? destination,
        DbTransaction transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(transaction);

        var store = storeResolver.Resolve(outboxName);

        var outboxMessage = new OutboxMessage
        {
            OutboxName = outboxName,
            Type = typeof(TMessage).Name,
            Payload = JsonSerializer.Serialize(message),
            Destination = destination,
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString(),
            CorrelationId = OutboxCorrelationContext.Current,
        };

        await store.InsertAsync(outboxMessage, transaction.Connection!, transaction, ct);

        var opts = optionsResolver.TryResolve(outboxName);
        if (opts?.OnMessageStored is not null)
            await opts.OnMessageStored(outboxMessage);

        return OutboxWriteResult.Written(outboxMessage.Id, scheduled: false);
    }
}
