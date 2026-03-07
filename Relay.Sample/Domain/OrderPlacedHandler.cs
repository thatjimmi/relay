using Relay.Inbox.Core;
using Relay.Outbox.Core;

namespace Relay.Sample.Domain;

/// <summary>
/// Processes an incoming OrderPlaced event.
/// Writes two outbox messages as part of the same logical unit:
///   1. OrderFulfillmentRequested  → warehouse service
///   2. OrderConfirmationSent      → notifications service
///
/// The outbox writer automatically correlates these messages back to this
/// inbox message via OutboxCorrelationContext.
/// </summary>
public sealed class OrderPlacedHandler(IOutboxWriter outbox) : IInboxHandler<OrderPlaced>
{
    public string GetIdempotencyKey(OrderPlaced msg) =>
        $"order-placed:{msg.OrderId}";

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
