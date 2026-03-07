using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;

namespace Relay.Sample.AzureFunctions.Domain;

/// <summary>
/// Simulates delivery to the warehouse service.
/// In production: publish to Service Bus, RabbitMQ, HTTP call, etc.
/// </summary>
public sealed class FulfillmentPublisher(ILogger<FulfillmentPublisher> logger)
    : IOutboxPublisher<OrderFulfillmentRequested>
{
    public Task PublishAsync(
        OrderFulfillmentRequested message,
        OutboxMessage envelope,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[warehouse] Fulfil order {OrderId} for {CustomerId} ({ItemCount} item(s)) → {Destination}",
            message.OrderId, message.CustomerId, message.Items.Length, envelope.Destination);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Simulates delivery to the notifications service.
/// </summary>
public sealed class ConfirmationPublisher(ILogger<ConfirmationPublisher> logger)
    : IOutboxPublisher<OrderConfirmationSent>
{
    public Task PublishAsync(
        OrderConfirmationSent message,
        OutboxMessage envelope,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[notify] Email confirmation for order {OrderId} to {CustomerId}, total £{Total:F2} → {Destination}",
            message.OrderId, message.CustomerId, message.Total, envelope.Destination);

        return Task.CompletedTask;
    }
}
