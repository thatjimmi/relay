namespace Relay.Inbox.Core;

public record InboxReceiveResult
{
    public bool Accepted { get; init; }
    public bool WasDuplicate { get; init; }
    public Guid? MessageId { get; init; }

    public static InboxReceiveResult Duplicate() =>
        new() { Accepted = false, WasDuplicate = true };

    public static InboxReceiveResult Stored(Guid id) =>
        new() { Accepted = true, WasDuplicate = false, MessageId = id };
}

public record InboxProcessResult
{
    public int Processed { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<InboxFailure> Failures { get; init; } = [];

    public bool HasFailures => Failures.Count > 0;
    public int Total => Processed + Failed;
}

public record InboxFailure(
    Guid MessageId,
    string MessageType,
    string Error,
    bool DeadLettered);

public record InboxStats(
    string InboxName,
    int Pending,
    int Processing,
    int Processed,
    int Failed,
    int DeadLettered,
    DateTime? OldestPending);
