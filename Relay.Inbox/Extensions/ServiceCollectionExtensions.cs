using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Inbox.Internal;
using Relay.Inbox.Storage;

namespace Relay.Inbox.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register a named inbox with its handlers and configuration.
    /// </summary>
    /// <example>
    /// builder.Services
    ///     .AddInbox("market-exchange", inbox => inbox
    ///         .WithHandler&lt;TradeExecutedEvent, TradeExecutedHandler&gt;()
    ///         .WithHandler&lt;OrderFilledEvent, OrderFilledHandler&gt;()
    ///         .OnDeadLettered((msg, ex) => alertService.NotifyAsync(msg)))
    ///     .UseSqlInboxStore("Server=.;Database=MyApp;...")
    ///     .UseInMemoryInboxStore(); // for tests
    /// </example>
    public static IServiceCollection AddInbox(
        this IServiceCollection services,
        string inboxName,
        Action<InboxBuilder> configure)
    {
        // Ensure shared singletons are registered once
        HandlerRegistry registry;
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(HandlerRegistry));
        if (existing == null)
        {
            registry = new HandlerRegistry();
            services.AddSingleton(registry);
            services.AddScoped<IInboxProcessor, InboxProcessor>();
        }
        else
        {
            registry = (HandlerRegistry)existing.ImplementationInstance!;
        }

        var builder = new InboxBuilder(services, inboxName, registry);
        configure(builder);
        return services;
    }

    /// <summary>
    /// Use SQL Server as the inbox store (Microsoft.Data.SqlClient, no ORM).
    /// </summary>
    public static IServiceCollection UseSqlInboxStore(
        this IServiceCollection services,
        string connectionString,
        Action<SqlInboxStoreOptions>? configure = null)
    {
        var opts = new SqlInboxStoreOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        services.AddSingleton(opts);
        services.AddSingleton<IInboxStore, SqlInboxStore>();

        if (opts.AutoMigrateSchema)
        {
            // Schema is created lazily on first use via IHostedService or manually
            services.AddHostedService<InboxSchemaInitializer>();
        }

        return services;
    }

    /// <summary>
    /// Use in-memory store — ideal for tests and local development.
    /// The InMemoryInboxStore instance is registered as a singleton so you can
    /// inject it directly in tests to inspect state.
    /// </summary>
    public static IServiceCollection UseInMemoryInboxStore(this IServiceCollection services)
    {
        var store = new InMemoryInboxStore();
        services.AddSingleton<IInboxStore>(store);
        services.AddSingleton(store); // also register as concrete type for test access
        return services;
    }
}

// -------------------------------------------------------------------------
// Fluent builder
// -------------------------------------------------------------------------

public sealed class InboxBuilder
{
    private readonly IServiceCollection services;
    private readonly string inboxName;
    private readonly HandlerRegistry registry;

    private InboxOptions _options = new();

    internal InboxBuilder(IServiceCollection services, string inboxName, HandlerRegistry registry)
    {
        this.services = services;
        this.inboxName = inboxName;
        this.registry = registry;
    }

    /// <summary>Register a handler for a message type in this inbox.</summary>
    public InboxBuilder WithHandler<TMessage, THandler>()
        where THandler : class, IInboxHandler<TMessage>
    {
        services.AddScoped<IInboxHandler<TMessage>, THandler>();

        // Register the receiver for this specific message type
        services.AddScoped<IInboxReceiver<TMessage>>(sp =>
        {
            var store   = sp.GetRequiredService<IInboxStore>();
            var handler = sp.GetRequiredService<IInboxHandler<TMessage>>();
            return new InboxReceiver<TMessage>(store, handler, inboxName, _options);
        });

        // Tell the registry what CLR type maps to this message name in this inbox
        registry.Register(inboxName, typeof(TMessage));

        return this;
    }

    /// <summary>Configure retry limit, hooks, etc.</summary>
    public InboxBuilder WithOptions(Action<InboxOptions> configure)
    {
        configure(_options);
        services.AddSingleton(_options);
        return this;
    }

    /// <summary>Called after every successful message processing.</summary>
    public InboxBuilder OnProcessed(Func<InboxMessage, Task> hook)
    {
        _options.OnProcessed = hook;
        return this;
    }

    /// <summary>Called when a message fails (before retry/dead-letter decision).</summary>
    public InboxBuilder OnFailed(Func<InboxMessage, Exception, Task> hook)
    {
        _options.OnFailed = hook;
        return this;
    }

    /// <summary>
    /// Called when a message is dead-lettered.
    /// Use this to fire alerts, push to a queue, send to Slack, etc.
    /// </summary>
    public InboxBuilder OnDeadLettered(Func<InboxMessage, Exception, Task> hook)
    {
        _options.OnDeadLettered = hook;
        return this;
    }

    /// <summary>Max retries before dead-lettering. Default: 5.</summary>
    public InboxBuilder WithMaxRetries(int retries)
    {
        _options.MaxRetries = retries;
        return this;
    }
}

// -------------------------------------------------------------------------
// Schema initializer
// -------------------------------------------------------------------------

internal sealed class InboxSchemaInitializer(IInboxStore store, ILogger<InboxSchemaInitializer> logger)
    : Microsoft.Extensions.Hosting.IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("Ensuring inbox schema exists...");
        await store.EnsureSchemaAsync(ct);
        logger.LogInformation("Inbox schema ready.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
