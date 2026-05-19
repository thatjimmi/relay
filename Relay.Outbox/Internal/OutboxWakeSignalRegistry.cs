using System.Collections.Concurrent;

namespace Relay.Outbox.Internal;

/// <summary>
/// Resolves (or creates) an <see cref="OutboxWakeSignal"/> per named outbox.
/// Registered as a singleton so the signal instance is shared between
/// the writer (which wakes) and the dispatcher (which waits).
/// </summary>
internal sealed class OutboxWakeSignalRegistry
{
    private readonly ConcurrentDictionary<string, OutboxWakeSignal> _signals = new();

    public OutboxWakeSignal GetOrCreate(string outboxName) =>
        _signals.GetOrAdd(outboxName, _ => new OutboxWakeSignal());
}
