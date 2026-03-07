using Relay.Inbox.Core;

namespace Relay.Inbox.Internal;

/// <summary>
/// Maps inbox names to store instances. Supports a <c>"*"</c> wildcard entry
/// that acts as the global default when no inbox-specific store is registered.
/// </summary>
internal sealed class InboxStoreResolver
{
    private readonly Dictionary<string, IInboxStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a store under a specific inbox name, or <c>"*"</c> for the global default.
    /// </summary>
    public void Register(string nameOrWildcard, IInboxStore store) =>
        _stores[nameOrWildcard] = store;

    /// <summary>
    /// Resolve the store for the given inbox name.
    /// Falls back to the <c>"*"</c> default if no name-specific store is registered.
    /// </summary>
    public IInboxStore Resolve(string inboxName) =>
        _stores.TryGetValue(inboxName, out var store) ? store
        : _stores.TryGetValue("*", out var fallback) ? fallback
        : throw new InvalidOperationException(
            $"No store registered for inbox '{inboxName}'. " +
            "Call .UseSqlStore() on the inbox builder, or register a global store with .UseSqlInboxStore().");

    /// <summary>
    /// All distinct store instances — used by the schema initializer.
    /// </summary>
    public IReadOnlyCollection<IInboxStore> GetAll() => _stores.Values.Distinct().ToList();
}
