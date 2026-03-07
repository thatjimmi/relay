using Microsoft.Extensions.DependencyInjection;
using Relay.Inbox.Extensions;
using Relay.Inbox.Core;
using Relay.Outbox.Extensions;
using Relay.Outbox.Core;

namespace Relay;

/// <summary>
/// Fluent entry point for registering the full Relay inbox + outbox stack.
/// </summary>
public static class RelayServiceCollectionExtensions
{
    /// <summary>
    /// Add a named channel with both an inbox and an outbox.
    /// The channel name ties them together — the inbox stores incoming messages,
    /// the outbox stores outgoing events produced during processing.
    /// </summary>
    /// <example>
    /// builder.Services.AddRelay(relay => relay
    ///     .AddChannel("market-exchange", channel => channel
    ///         .Inbox(inbox => inbox
    ///             .WithHandler&lt;TradeExecutedEvent, TradeExecutedHandler&gt;()
    ///             .WithMaxRetries(5))
    ///         .Outbox(outbox => outbox
    ///             .WithPublisher&lt;TradeConfirmedEvent, RabbitMqTradePublisher&gt;()
    ///             .OnDeadLettered((msg, ex) => alerts.NotifyAsync(msg))))
    ///     .UseSqlStore(connectionString));
    /// </example>
    public static IServiceCollection AddRelay(
        this IServiceCollection services,
        Action<RelayBuilder> configure)
    {
        var builder = new RelayBuilder(services);
        configure(builder);
        return services;
    }
}

// -------------------------------------------------------------------------
// Top-level builder
// -------------------------------------------------------------------------

public sealed class RelayBuilder(IServiceCollection services)
{
    private string? _connectionString;

    public RelayBuilder AddChannel(string channelName, Action<ChannelBuilder> configure)
    {
        var channelBuilder = new ChannelBuilder(services, channelName);
        configure(channelBuilder);
        return this;
    }

    /// <summary>Use SQL Server for both inbox and outbox stores.</summary>
    public RelayBuilder UseSqlStore(
        string connectionString,
        Action<SqlStoreOptions>? configure = null)
    {
        var opts = new SqlStoreOptions();
        configure?.Invoke(opts);

        services.UseSqlInboxStore(connectionString, o =>
        {
            o.TableName        = opts.InboxTableName;
            o.AutoMigrateSchema = opts.AutoMigrateSchema;
            o.UseSkipLocked    = opts.UseSkipLocked;
        });

        services.UseSqlOutboxStore(connectionString, o =>
        {
            o.TableName        = opts.OutboxTableName;
            o.AutoMigrateSchema = opts.AutoMigrateSchema;
            o.UseSkipLocked    = opts.UseSkipLocked;
        });

        return this;
    }

    /// <summary>Use in-memory stores for both inbox and outbox — ideal for tests.</summary>
    public RelayBuilder UseInMemoryStore()
    {
        services.UseInMemoryInboxStore();
        services.UseInMemoryOutboxStore();
        return this;
    }
}

// -------------------------------------------------------------------------
// Channel builder
// -------------------------------------------------------------------------

public sealed class ChannelBuilder(IServiceCollection services, string channelName)
{
    /// <summary>Configure the inbox side of this channel.</summary>
    public ChannelBuilder Inbox(Action<InboxBuilder> configure)
    {
        services.AddInbox(channelName, configure);
        return this;
    }

    /// <summary>Configure the outbox side of this channel.</summary>
    public ChannelBuilder Outbox(Action<OutboxBuilder> configure)
    {
        services.AddOutbox(channelName, configure);
        return this;
    }
}

// -------------------------------------------------------------------------
// Shared SQL options (applies to both stores)
// -------------------------------------------------------------------------

public sealed class SqlStoreOptions
{
    public string InboxTableName  { get; set; } = "InboxMessages";
    public string OutboxTableName { get; set; } = "OutboxMessages";
    public bool AutoMigrateSchema { get; set; } = true;
    public bool UseSkipLocked     { get; set; } = true;
}
