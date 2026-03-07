using Relay.Outbox.Core;

namespace Relay.Outbox.Internal;

internal sealed class PublisherRegistry
{
    private readonly Dictionary<(string outbox, string type), Type> _messageTypes = new();

    public void Register(string outboxName, Type messageType)
    {
        var key = (outboxName.ToLowerInvariant(), messageType.Name);
        _messageTypes[key] = messageType;
    }

    public Type? Resolve(string outboxName, string typeName)
    {
        _messageTypes.TryGetValue((outboxName.ToLowerInvariant(), typeName), out var type);
        return type;
    }

    public IEnumerable<string> GetRegisteredOutboxes() =>
        _messageTypes.Keys.Select(k => k.outbox).Distinct();
}
