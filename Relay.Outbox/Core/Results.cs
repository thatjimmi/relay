namespace Relay.Outbox.Core;

public record OutboxWriteResult
{
    public Guid MessageId { get; init; }
    public bool IsScheduled { get; init; }

    public static OutboxWriteResult Written(Guid id, bool scheduled = false) =>
        new() { MessageId = id, IsScheduled = scheduled };
}

public record OutboxDispatchResult
{
    public int Published { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<OutboxFailure> Failures { get; init; } = [];

    public bool HasFailures => Failures.Count > 0;
    public int Total => Published + Failed;
}

public record OutboxFailure(
    Guid MessageId,
    string MessageType,
    string Error,
    bool DeadLettered);

public record OutboxStats(
    string OutboxName,
    int Pending,
    int Dispatching,
    int Published,
    int Failed,
    int DeadLettered,
    int Scheduled,
    DateTime? OldestPending);
