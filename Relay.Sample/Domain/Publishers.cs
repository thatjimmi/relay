using Relay.Outbox.Core;

namespace Relay.Sample.Domain;

/// <summary>
/// Simulates delivery to the warehouse service.
/// In production this would publish to RabbitMQ, Azure Service Bus, HTTP, etc.
/// </summary>
public sealed class FulfillmentPublisher : IOutboxPublisher<OrderFulfillmentRequested>
{
    public Task PublishAsync(
        OrderFulfillmentRequested message,
        OutboxMessage envelope,
        CancellationToken ct = default)
    {
        Console.WriteLine(
            $"  [warehouse]  Fulfil order {message.OrderId} " +
            $"for customer {message.CustomerId} " +
            $"({message.Items.Length} item(s)) " +
            $"→ dest: {envelope.Destination}");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Simulates delivery to the notifications service.
/// </summary>
public sealed class ConfirmationPublisher : IOutboxPublisher<OrderConfirmationSent>
{
    public Task PublishAsync(
        OrderConfirmationSent message,
        OutboxMessage envelope,
        CancellationToken ct = default)
    {
        Console.WriteLine(
            $"  [notify]     Email confirmation for order {message.OrderId} " +
            $"to customer {message.CustomerId}, total £{message.Total:F2} " +
            $"→ dest: {envelope.Destination}");

        return Task.CompletedTask;
    }
}
