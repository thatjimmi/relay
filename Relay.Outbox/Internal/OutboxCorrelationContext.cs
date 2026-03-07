namespace Relay.Outbox.Internal;

/// <summary>
/// Ambient async-local context that carries the current inbox message ID
/// so that any outbox messages written during handler execution are
/// automatically correlated back to the inbox message that caused them.
///
/// Set by the inbox processor (via Relay meta-package) when dispatching a handler.
/// IOutboxWriter reads this automatically — zero manual wiring needed.
/// </summary>
public static class OutboxCorrelationContext
{
    private static readonly AsyncLocal<string?> _current = new();

    /// <summary>The current correlation ID, if set.</summary>
    public static string? Current => _current.Value;

    /// <summary>
    /// Set the correlation ID for the current async execution context.
    /// Returns an IDisposable that clears the context when disposed.
    /// </summary>
    public static IDisposable Set(string correlationId)
    {
        _current.Value = correlationId;
        return new CorrelationScope();
    }

    private sealed class CorrelationScope : IDisposable
    {
        public void Dispose() => _current.Value = null;
    }
}
