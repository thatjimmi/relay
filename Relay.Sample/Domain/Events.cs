namespace Relay.Sample.Domain;

// ---------------------------------------------------------------------------
// Inbound events — arrive at the system boundary (API, webhook, queue consumer)
// ---------------------------------------------------------------------------

/// <summary>
/// Raised by an external checkout service when a customer places an order.
/// Received via the inbox to guarantee exactly-once processing.
/// </summary>
public record OrderPlaced(
    string OrderId,
    string CustomerId,
    string[] Items,
    decimal Total);

// ---------------------------------------------------------------------------
// Outbound events — staged in the outbox and delivered to downstream services
// ---------------------------------------------------------------------------

/// <summary>
/// Published to the warehouse service to trigger fulfilment.
/// </summary>
public record OrderFulfillmentRequested(
    string OrderId,
    string CustomerId,
    string[] Items);

/// <summary>
/// Published to the notifications service to email the customer.
/// </summary>
public record OrderConfirmationSent(
    string OrderId,
    string CustomerId,
    decimal Total);
