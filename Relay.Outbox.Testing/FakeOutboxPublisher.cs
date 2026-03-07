using Relay.Outbox.Core;

namespace Relay.Outbox.Testing;

/// <summary>
/// Drop-in test double for IOutboxPublisher&lt;T&gt;.
/// Records all messages that were dispatched so you can assert on them.
/// </summary>
public sealed class FakeOutboxPublisher<TMessage> : IOutboxPublisher<TMessage>
{
    private readonly List<(TMessage Message, OutboxMessage Envelope)> _published = [];

    public Task PublishAsync(TMessage message, OutboxMessage envelope, CancellationToken ct = default)
    {
        _published.Add((message, envelope));
        return Task.CompletedTask;
    }

    /// <summary>All messages that were dispatched to this publisher.</summary>
    public IReadOnlyList<(TMessage Message, OutboxMessage Envelope)> Published => _published.AsReadOnly();

    /// <summary>The raw message payloads, for simple assertions.</summary>
    public IReadOnlyList<TMessage> Messages => _published.Select(p => p.Message).ToList().AsReadOnly();

    public int CallCount => _published.Count;

    public void Reset() => _published.Clear();
}

/// <summary>
/// Publisher that always throws — for testing retry and dead-letter paths.
/// </summary>
public sealed class AlwaysFailingPublisher<TMessage> : IOutboxPublisher<TMessage>
{
    private readonly string? _errorMessage;
    public AlwaysFailingPublisher(string? errorMessage = null) => _errorMessage = errorMessage;

    public Task PublishAsync(TMessage message, OutboxMessage envelope, CancellationToken ct = default) =>
        throw new Exception(_errorMessage ?? $"Simulated publish failure for {typeof(TMessage).Name}");
}

/// <summary>
/// Publisher that fails N times then succeeds — for testing retry behaviour.
/// </summary>
public sealed class FailThenSucceedPublisher<TMessage> : IOutboxPublisher<TMessage>
{
    private readonly int _failTimes;
    private int _callCount;
    private readonly List<TMessage> _published = [];

    public FailThenSucceedPublisher(int failTimes) => _failTimes = failTimes;

    public Task PublishAsync(TMessage message, OutboxMessage envelope, CancellationToken ct = default)
    {
        _callCount++;
        if (_callCount <= _failTimes)
            throw new Exception($"Simulated failure #{_callCount}");

        _published.Add(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<TMessage> Published => _published.AsReadOnly();
    public int TotalAttempts => _callCount;
}
