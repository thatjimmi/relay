using Relay.Inbox.Core;

namespace Relay.Inbox.Internal;

internal sealed class HandlerRegistry
{
    // Key: (inboxName, messageTypeName) → CLR message type
    private readonly Dictionary<(string inbox, string type), Type> _messageTypes = new();

    // Key: (inboxName, messageTypeName) → concrete handler implementation type
    private readonly Dictionary<(string inbox, string type), Type> _handlerTypes = new();

    public void Register(string inboxName, Type messageType, Type? handlerImplementationType = null)
    {
        var key = (inboxName.ToLowerInvariant(), messageType.Name);
        _messageTypes[key] = messageType;
        if (handlerImplementationType is not null)
            _handlerTypes[key] = handlerImplementationType;
    }

    public Type? Resolve(string inboxName, string typeName)
    {
        _messageTypes.TryGetValue((inboxName.ToLowerInvariant(), typeName), out var type);
        return type;
    }

    public Type? ResolveHandler(string inboxName, string typeName)
    {
        _handlerTypes.TryGetValue((inboxName.ToLowerInvariant(), typeName), out var type);
        return type;
    }

    public IEnumerable<string> GetRegisteredInboxes() =>
        _messageTypes.Keys.Select(k => k.inbox).Distinct();
}
