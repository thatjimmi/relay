using Relay.Inbox.Extensions;
using Relay.Outbox.Extensions;

namespace Relay.Sample.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>
    /// Use a SQLite-backed store for this inbox.
    /// Each inbox can have its own table by passing a different <paramref name="tableName"/>.
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
