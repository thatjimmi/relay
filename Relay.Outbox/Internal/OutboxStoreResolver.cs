using Relay.Outbox.Core;

namespace Relay.Outbox.Internal;

/// <summary>
/// Maps outbox names to store instances. Supports a <c>"*"</c> wildcard entry
/// that acts as the global default when no outbox-specific store is registered.
/// </summary>
internal sealed class OutboxStoreResolver
{
    private readonly Dictionary<string, IOutboxStore> _stores = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Register a store under a specific outbox name, or <c>"*"</c> for the global default.
    /// </summary>
    public void Register(string nameOrWildcard, IOutboxStore store) =>
        _stores[nameOrWildcard] = store;

    /// <summary>
    /// Resolve the store for the given outbox name.
    /// Falls back to the <c>"*"</c> default if no name-specific store is registered.
    /// </summary>
    public IOutboxStore Resolve(string outboxName) =>
        _stores.TryGetValue(outboxName, out var store) ? store
        : _stores.TryGetValue("*", out var fallback) ? fallback
        : throw new InvalidOperationException(
            $"No store registered for outbox '{outboxName}'. " +
            "Call .UseSqlStore() on the outbox builder, or register a global store with .UseSqlOutboxStore().");

    /// <summary>
    /// All distinct store instances — used by the schema initializer.
    /// </summary>
    public IReadOnlyCollection<IOutboxStore> GetAll() => _stores.Values.Distinct().ToList();
}
