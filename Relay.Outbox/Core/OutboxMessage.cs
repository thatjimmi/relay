namespace Relay.Outbox.Core;

public sealed class OutboxMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Named channel this message belongs to — e.g. "market-exchange", "payments".
    /// Mirrors the inbox pattern so the same name ties the two sides together.
    /// </summary>
    public required string OutboxName { get; init; }

    /// <summary>CLR type name of the original message — used to route to the right publisher.</summary>
    public required string Type { get; init; }

    /// <summary>JSON-serialized message payload.</summary>
    public required string Payload { get; init; }

    /// <summary>
    /// Correlation ID linking this outbox message to the inbox message that caused it.
    /// Set automatically when using IOutboxWriter inside an inbox handler.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>Distributed trace ID — captured automatically from Activity.Current.</summary>
    public string? TraceId { get; init; }

    /// <summary>
    /// Optional target destination hint passed to the publisher
    /// e.g. a topic name, exchange name, queue name.
    /// </summary>
    public string? Destination { get; init; }

    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.Pending;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }
    public string? Error { get; set; }
    public int RetryCount { get; set; }

    /// <summary>
    /// Optional delay — message will not be dispatched before this time.
    /// Useful for scheduling deferred events.
    /// </summary>
    public DateTime? ScheduledFor { get; init; }
}

public enum OutboxMessageStatus
{
    Pending,
    Dispatching,
    Published,
    Failed,
    DeadLettered
}
