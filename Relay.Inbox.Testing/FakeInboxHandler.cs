using Relay.Inbox.Core;

namespace Relay.Inbox.Testing;

/// <summary>
/// Drop-in test double for IInboxHandler&lt;T&gt;.
/// Records all handled messages so you can assert on them.
/// </summary>
public sealed class FakeInboxHandler<TMessage>(
    Func<TMessage, string> keyFn,
    Func<TMessage, Task>? onHandle = null) : IInboxHandler<TMessage>
{
    private readonly List<TMessage> _handled = [];
    private readonly List<TMessage> _keys    = [];

    public string GetIdempotencyKey(TMessage message) => keyFn(message);

    public async Task HandleAsync(TMessage message, CancellationToken ct = default)
    {
        _handled.Add(message);
        if (onHandle is not null)
            await onHandle(message);
    }

    /// <summary>All messages that were successfully dispatched to this handler.</summary>
    public IReadOnlyList<TMessage> Handled => _handled.AsReadOnly();

    /// <summary>How many times HandleAsync was called.</summary>
    public int CallCount => _handled.Count;

    /// <summary>Reset recorded state between tests.</summary>
    public void Reset() => _handled.Clear();
}

/// <summary>
/// Handler that always throws — useful for testing retry and dead-letter logic.
/// </summary>
public sealed class AlwaysFailingHandler<TMessage>(
    Func<TMessage, string> keyFn,
    string? errorMessage = null) : IInboxHandler<TMessage>
{
    public string GetIdempotencyKey(TMessage message) => keyFn(message);

    public Task HandleAsync(TMessage message, CancellationToken ct = default) =>
        throw new Exception(errorMessage ?? $"Simulated failure handling {typeof(TMessage).Name}");
}

/// <summary>
/// Handler that fails N times then succeeds — useful for testing retry behaviour.
/// </summary>
public sealed class FailThenSucceedHandler<TMessage>(
    Func<TMessage, string> keyFn,
    int failTimes) : IInboxHandler<TMessage>
{
    private int _callCount;
    private readonly List<TMessage> _handled = [];

    public string GetIdempotencyKey(TMessage message) => keyFn(message);

    public Task HandleAsync(TMessage message, CancellationToken ct = default)
    {
        _callCount++;
        if (_callCount <= failTimes)
            throw new Exception($"Simulated failure #{_callCount}");

        _handled.Add(message);
        return Task.CompletedTask;
    }

    public IReadOnlyList<TMessage> Handled => _handled.AsReadOnly();
    public int TotalAttempts => _callCount;
}
