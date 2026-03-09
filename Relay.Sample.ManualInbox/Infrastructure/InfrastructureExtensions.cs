using Microsoft.Extensions.DependencyInjection;
using Relay.Inbox.Core;
using Relay.Inbox.Extensions;

namespace Relay.Sample.ManualInbox.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>
    /// Use a SQLite-backed store for this inbox (builder / handled-mode variant).
    /// Schema is created automatically on startup via the library's built-in schema initializer.
    /// </summary>
    public static InboxBuilder UseSqliteStore(
        this InboxBuilder builder,
        string connectionString,
        string tableName = "InboxMessages")
    {
        return builder.UseStore(new SqliteInboxStore(connectionString, tableName));
    }

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
}
