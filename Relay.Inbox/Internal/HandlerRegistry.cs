using Relay.Inbox.Core;

namespace Relay.Inbox.Internal;

internal sealed class HandlerRegistry
{
    // Key: (inboxName, messageTypeName) → (CLR type, handler factory)
    private readonly Dictionary<(string inbox, string type), Type> _messageTypes = new();

    public void Register(string inboxName, Type messageType)
    {
        var key = (inboxName.ToLowerInvariant(), messageType.Name);
        _messageTypes[key] = messageType;
    }

    public Type? Resolve(string inboxName, string typeName)
    {
        _messageTypes.TryGetValue((inboxName.ToLowerInvariant(), typeName), out var type);
        return type;
    }

    public IEnumerable<string> GetRegisteredInboxes() =>
        _messageTypes.Keys.Select(k => k.inbox).Distinct();
}
