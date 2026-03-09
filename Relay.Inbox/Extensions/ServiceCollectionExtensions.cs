using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// // Global store (all inboxes share one table):
    /// builder.Services
    ///     .AddInbox("market-exchange", inbox => inbox
    ///         .WithHandler&lt;TradeExecutedEvent, TradeExecutedHandler&gt;()
    ///         .WithHandler&lt;OrderFilledEvent, OrderFilledHandler&gt;()
    ///         .OnDeadLettered((msg, ex) => alertService.NotifyAsync(msg)))
    ///     .UseSqlInboxStore("Server=.;Database=MyApp;...");
    ///
    /// // Per-inbox stores (each inbox gets its own table):
    /// builder.Services
    ///     .AddInbox("orders", inbox => inbox
    ///         .WithHandler&lt;OrderPlaced, OrderHandler&gt;()
    ///         .UseSqlStore("Server=.;Database=MyApp;...", o => o.TableName = "OrderInbox"))
    ///     .AddInbox("payments", inbox => inbox
    ///         .WithHandler&lt;PaymentReceived, PaymentHandler&gt;()
    ///         .UseSqlStore("Server=.;Database=MyApp;...", o => o.TableName = "PaymentInbox"));
    /// </example>
    public static IServiceCollection AddInbox(
        this IServiceCollection services,
        string inboxName,
        Action<InboxBuilder> configure)
    {
        // Ensure shared singletons are registered once
        HandlerRegistry registry;
        var existingRegistry = services.FirstOrDefault(d => d.ServiceType == typeof(HandlerRegistry));
        if (existingRegistry == null)
        {
            registry = new HandlerRegistry();
            services.AddSingleton(registry);
            services.AddScoped<IInboxProcessor, InboxProcessor>();
        }
        else
        {
            registry = (HandlerRegistry)existingRegistry.ImplementationInstance!;
        }

        // Ensure a default InboxOptions is always available so InboxProcessor
        // can be resolved even when WithOptions/hooks are never called.
        services.TryAddSingleton(new InboxOptions());

        var storeResolver = EnsureStoreResolver(services);

        var builder = new InboxBuilder(services, inboxName, registry, storeResolver);
        configure(builder);
        return services;
    }

    /// <summary>
    /// Register the raw-mode inbox client (<see cref="IInboxClient"/>) without any handler
    /// infrastructure. Use this when you want to receive, fetch, and acknowledge messages
    /// yourself — no <see cref="IInboxHandler{TMessage}"/> implementations needed.
    /// <para>
    /// You still need to configure a store via <c>.UseSqlInboxStore()</c> or
    /// <c>.UseInMemoryInboxStore()</c>.
    /// </para>
    /// </summary>
    /// <example>
    /// builder.Services
    ///     .AddInboxClient()
    ///     .UseSqlInboxStore("Server=.;Database=MyApp;...");
    /// </example>
    public static IServiceCollection AddInboxClient(
        this IServiceCollection services,
        Action<InboxOptions>? configure = null)
    {
        var options = new InboxOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(options);
        EnsureStoreResolver(services);
        services.TryAddScoped<IInboxClient, InboxClient>();
        return services;
    }

    /// <summary>
    /// Register a custom <see cref="IInboxStore"/> as the global default store for all inboxes.
    /// Useful when using <see cref="AddInboxClient"/> with a store implementation that lives
    /// outside the main package (e.g. a SQLite or MongoDB store in a sample/host project).
    /// </summary>
    public static IServiceCollection UseInboxStore(
        this IServiceCollection services,
        IInboxStore store)
    {
        services.AddSingleton<IInboxStore>(store);
        services.AddSingleton(store.GetType(), store);

        var resolver = EnsureStoreResolver(services);
        resolver.Register("*", store);

        EnsureSchemaInitializerInternal(services);
        return services;
    }

    private static InboxStoreResolver EnsureStoreResolver(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(InboxStoreResolver));
        if (existing != null)
            return (InboxStoreResolver)existing.ImplementationInstance!;

        var resolver = new InboxStoreResolver();
        services.AddSingleton(resolver);
        return resolver;
    }

    /// <summary>
    /// Use SQL Server as the global/default inbox store (Microsoft.Data.SqlClient, no ORM).
    /// All inboxes without a builder-level <c>.UseSqlStore()</c> will fall back to this store.
    /// </summary>
    public static IServiceCollection UseSqlInboxStore(
        this IServiceCollection services,
        string connectionString,
        Action<SqlInboxStoreOptions>? configure = null)
    {
        var opts = new SqlInboxStoreOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var store = new SqlInboxStore(opts);

        services.AddSingleton(opts);
        services.AddSingleton<IInboxStore>(store);

        // Register as the global default in the resolver
        var resolver = EnsureStoreResolver(services);
        resolver.Register("*", store);

        if (opts.AutoMigrateSchema)
            EnsureSchemaInitializer(services);

        return services;
    }

    /// <summary>
    /// Use in-memory store as the global default — ideal for tests and local development.
    /// The InMemoryInboxStore instance is registered as a singleton so you can
    /// inject it directly in tests to inspect state.
    /// </summary>
    public static IServiceCollection UseInMemoryInboxStore(this IServiceCollection services)
    {
        var store = new InMemoryInboxStore();
        services.AddSingleton<IInboxStore>(store);
        services.AddSingleton(store); // also register as concrete type for test access

        var resolver = EnsureStoreResolver(services);
        resolver.Register("*", store);

        return services;
    }

    private static void EnsureSchemaInitializer(IServiceCollection services) =>
        EnsureSchemaInitializerInternal(services);

    internal static void EnsureSchemaInitializerInternal(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.Hosting.IHostedService)
                             && d.ImplementationType == typeof(InboxSchemaInitializer)))
        {
            services.AddHostedService<InboxSchemaInitializer>();
        }
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
    private readonly InboxStoreResolver storeResolver;

    private InboxOptions _options = new();

    internal InboxBuilder(IServiceCollection services, string inboxName, HandlerRegistry registry, InboxStoreResolver storeResolver)
    {
        this.services = services;
        this.inboxName = inboxName;
        this.registry = registry;
        this.storeResolver = storeResolver;
    }

    /// <summary>
    /// Use a dedicated SQL Server store for this inbox (separate table).
    /// Overrides the global store registered via <c>UseSqlInboxStore()</c> for this inbox only.
    /// </summary>
    public InboxBuilder UseSqlStore(string connectionString, Action<SqlInboxStoreOptions>? configure = null)
    {
        var opts = new SqlInboxStoreOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var store = new SqlInboxStore(opts);
        storeResolver.Register(inboxName, store);

        if (opts.AutoMigrateSchema)
            ServiceCollectionExtensions.EnsureSchemaInitializerInternal(services);

        return this;
    }

    /// <summary>
    /// Use a dedicated in-memory store for this inbox.
    /// Useful for testing individual inboxes in isolation.
    /// </summary>
    public InboxBuilder UseInMemoryStore()
    {
        var store = new InMemoryInboxStore();
        storeResolver.Register(inboxName, store);
        return this;
    }

    /// <summary>
    /// Use a custom <see cref="IInboxStore"/> implementation for this inbox.
    /// Overrides the global store for this inbox only.
    /// The store's <c>EnsureSchemaAsync</c> will be called automatically on startup.
    /// </summary>
    public InboxBuilder UseStore(IInboxStore store)
    {
        storeResolver.Register(inboxName, store);
        ServiceCollectionExtensions.EnsureSchemaInitializerInternal(services);
        return this;
    }

    /// <summary>Register a handler for a message type in this inbox.</summary>
    public InboxBuilder WithHandler<TMessage, THandler>()
        where THandler : class, IInboxHandler<TMessage>
    {
        services.AddScoped<IInboxHandler<TMessage>, THandler>();

        // Register the receiver for this specific message type.
        // The store is resolved by inbox name at runtime via the resolver.
        services.AddScoped<IInboxReceiver<TMessage>>(sp =>
        {
            var resolver = sp.GetRequiredService<InboxStoreResolver>();
            var store = resolver.Resolve(inboxName);
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

internal sealed class InboxSchemaInitializer(InboxStoreResolver storeResolver, ILogger<InboxSchemaInitializer> logger)
    : Microsoft.Extensions.Hosting.IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var stores = storeResolver.GetAll();
        logger.LogInformation("Ensuring inbox schema exists for {Count} store(s)...", stores.Count);
        foreach (var store in stores)
            await store.EnsureSchemaAsync(ct);
        logger.LogInformation("Inbox schema ready.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
