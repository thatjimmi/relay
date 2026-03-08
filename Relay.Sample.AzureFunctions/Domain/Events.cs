namespace Relay.Sample.AzureFunctions.Domain;

// Inbound — arrives at the HTTP trigger boundary
public record OrderPlaced(
    string    OrderId,
    string    CustomerId,
    string[]  Items,
    decimal   Total,
    DateTime? SourceTimestamp = null);

// Outbound — staged in the outbox, delivered by Timer Trigger dispatcher
public record OrderFulfillmentRequested(
    string   OrderId,
    string   CustomerId,
    string[] Items);

public record OrderConfirmationSent(
    string  OrderId,
    string  CustomerId,
    decimal Total);
