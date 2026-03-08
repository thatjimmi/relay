namespace Relay.Inbox.Core;

public sealed class InboxOptions
{
    /// <summary>How many times to retry a failing message before dead-lettering it.</summary>
    public int MaxRetries { get; set; } = 5;

    /// <summary>
    /// Optional hook called after every successful handle.
    /// Useful for metrics, logging, audit trails.
    /// </summary>
    public Func<InboxMessage, Task>? OnProcessed { get; set; }

    /// <summary>
    /// Optional hook called when a message fails (before retry/dead-letter decision).
    /// </summary>
    public Func<InboxMessage, Exception, Task>? OnFailed { get; set; }

    /// <summary>
    /// Optional hook called when a message is dead-lettered.
    /// Use this to alert on-call, push to a dead-letter queue, etc.
    /// </summary>
    public Func<InboxMessage, Exception, Task>? OnDeadLettered { get; set; }

    /// <summary>
    /// Optional hook called immediately after a message is persisted to the store.
    /// Useful for triggering downstream work (e.g. publish a Service Bus event, wake a processor).
    /// </summary>
    public Func<InboxMessage, Task>? OnMessageStored { get; set; }

    /// <summary>
    /// Optional hook called when a duplicate is received.
    /// </summary>
    public Func<string, Task>? OnDuplicate { get; set; }

    /// <summary>
    /// Optional hook called when an existing message is updated due to a newer source timestamp.
    /// The message reflects the new payload; use this for metrics, logging, or triggering downstream work.
    /// </summary>
    public Func<InboxMessage, Task>? OnMessageUpdated { get; set; }

    /// <summary>
    /// Optional factory that sets an ambient correlation scope for the duration of handler
    /// execution. Called with the inbox message ID (as string) before the handler runs;
    /// the returned IDisposable is disposed immediately after.
    ///
    /// Use this to propagate the inbox message ID to the outbox writer so outbox messages
    /// are automatically correlated back to the inbox message that caused them.
    /// Example: options.CorrelationScope = OutboxCorrelationContext.Set;
    /// </summary>
    public Func<string, IDisposable>? CorrelationScope { get; set; }
}

public sealed class SqlInboxStoreOptions
{
    public required string ConnectionString { get; set; }

    /// <summary>Table name. Defaults to "InboxMessages".</summary>
    public string TableName { get; set; } = "InboxMessages";

    /// <summary>
    /// Auto-create the table on startup if it doesn't exist.
    /// Set to false if you manage schema via migrations.
    /// </summary>
    public bool AutoMigrateSchema { get; set; } = true;

    /// <summary>
    /// Use SKIP LOCKED when fetching pending messages.
    /// Enables safe parallel processing across multiple instances.
    /// Requires SQL Server 2019+ or Azure SQL.
    /// </summary>
    public bool UseSkipLocked { get; set; } = true;
}
