using Relay.Inbox.Core;
using Relay.Outbox.Core;

namespace Relay.Sample.AzureFunctions.Domain;

/// <summary>
/// Runs inside the inbox processor (Timer Trigger → ProcessInboxFunction).
/// Writes two outbox messages that will be dispatched by a separate Timer Trigger.
/// </summary>
public sealed class OrderPlacedHandler(IOutboxWriter outbox) : IInboxHandler<OrderPlaced>
{
    public string GetIdempotencyKey(OrderPlaced msg) => $"order-placed:{msg.OrderId}";

    public async Task HandleAsync(OrderPlaced msg, CancellationToken ct = default)
    {
        await outbox.WriteAsync(
            new OrderFulfillmentRequested(msg.OrderId, msg.CustomerId, msg.Items),
            outboxName: "orders",
            destination: "warehouse.fulfil",
            ct: ct);

        await outbox.WriteAsync(
            new OrderConfirmationSent(msg.OrderId, msg.CustomerId, msg.Total),
            outboxName: "orders",
            destination: "notifications.email",
            ct: ct);
    }
}
