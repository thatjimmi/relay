using Microsoft.Extensions.DependencyInjection;
using Relay.Inbox.Core;
using Relay.Inbox.Extensions;
using Relay.Outbox.Extensions;

namespace Relay.Sample.Trayport.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>
    /// Register a SQLite-backed store as the global default for all inboxes.
    /// Use this with <c>AddInboxClient()</c> (client / raw mode).
    /// </summary>
    public static IServiceCollection UseInboxClientSqliteStore(
        this IServiceCollection services,
        string connectionString,
        string tableName = "InboxMessages")
    {
        return services.UseInboxStore(new SqliteInboxStore(connectionString, tableName));
    }

    /// <summary>
    /// Use a SQLite-backed store for this outbox.
    /// Each outbox can have its own table by passing a different <paramref name="tableName"/>.
    /// Schema is created automatically on startup via the library's built-in schema initializer.
    /// </summary>
    public static OutboxBuilder UseSqliteStore(
        this OutboxBuilder builder,
        string connectionString,
        string tableName = "OutboxMessages")
    {
        return builder.UseStore(new SqliteOutboxStore(connectionString, tableName));
    }
}
