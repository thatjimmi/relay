using Relay.Outbox.Core;

namespace Relay.Outbox.Internal;

/// <summary>
/// Maps outbox names to their <see cref="OutboxOptions"/>.
/// Used by <see cref="OutboxWriter"/> to look up per-outbox hooks at write time.
/// </summary>
internal sealed class OutboxOptionsResolver
{
    private readonly Dictionary<string, OutboxOptions> _options = new(StringComparer.OrdinalIgnoreCase);

    public void Register(string outboxName, OutboxOptions options) =>
        _options[outboxName] = options;

    public OutboxOptions? TryResolve(string outboxName) =>
        _options.TryGetValue(outboxName, out var opts) ? opts : null;
}
