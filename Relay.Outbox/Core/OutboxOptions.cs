namespace Relay.Outbox.Core;

public sealed class OutboxOptions
{
    /// <summary>How many dispatch attempts before dead-lettering. Default: 5.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Called immediately after a message is persisted to the store.
    /// Useful for waking a dispatcher or emitting a downstream event.
    /// </summary>
    public Func<OutboxMessage, Task>? OnMessageStored { get; set; }

    /// <summary>Called after a message is successfully published.</summary>
    public Func<OutboxMessage, Task>? OnPublished { get; set; }

    /// <summary>Called when a dispatch attempt fails (before retry/dead-letter decision).</summary>
    public Func<OutboxMessage, Exception, Task>? OnFailed { get; set; }

    /// <summary>
    /// Called when a message is dead-lettered after exhausting retries.
    /// Use to alert on-call, push to a secondary queue, etc.
    /// </summary>
    public Func<OutboxMessage, Exception, Task>? OnDeadLettered { get; set; }
}

public sealed class SqlOutboxStoreOptions
{
    public required string ConnectionString { get; set; }

    /// <summary>Table name. Defaults to "OutboxMessages".</summary>
    public string TableName { get; set; } = "OutboxMessages";

    /// <summary>
    /// Auto-create the table on startup.
    /// Set false if managing schema via migrations.
    /// </summary>
    public bool AutoMigrateSchema { get; set; } = true;

    /// <summary>
    /// Use SKIP LOCKED when fetching pending messages.
    /// Enables safe parallel dispatch across multiple instances.
    /// Requires SQL Server 2019+ or Azure SQL.
    /// </summary>
    public bool UseSkipLocked { get; set; } = true;
}
