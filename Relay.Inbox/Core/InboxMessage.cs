namespace Relay.Inbox.Core;

public sealed class InboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string InboxName { get; init; }
    public required string Type { get; init; }
    public required string IdempotencyKey { get; init; }
    public required string Payload { get; set; }
    public InboxMessageStatus Status { get; set; } = InboxMessageStatus.Pending;
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ProcessedAt { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }
    public string? TraceId { get; init; }         // correlate with distributed traces
    public string? Source { get; init; }          // e.g. "binance-ws", "stripe-webhook"
    public DateTime? SourceTimestamp { get; set; } // timestamp from the external system
}

public enum InboxMessageStatus
{
    Pending,
    Processing,
    Processed,
    Failed,
    DeadLettered
}
