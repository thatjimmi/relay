using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;
using Relay.Outbox.Internal;
using Relay.Outbox.Storage;

namespace Relay.Outbox.Extensions;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Register a named outbox with its publishers and configuration.
    /// </summary>
    /// <example>
    /// // Global store (all outboxes share one table):
    /// builder.Services
    ///     .AddOutbox("market-exchange", outbox => outbox
    ///         .WithPublisher&lt;TradeConfirmedEvent, RabbitMqTradePublisher&gt;()
    ///         .WithPublisher&lt;PositionUpdatedEvent, RabbitMqPositionPublisher&gt;()
    ///         .OnDeadLettered((msg, ex) => alerts.NotifyAsync(msg)))
    ///     .UseSqlOutboxStore("Server=.;Database=MyApp;...");
    ///
    /// // Per-outbox stores (each outbox gets its own table):
    /// builder.Services
    ///     .AddOutbox("orders", outbox => outbox
    ///         .WithPublisher&lt;OrderConfirmed, OrderPublisher&gt;()
    ///         .UseSqlStore("Server=.;Database=MyApp;...", o => o.TableName = "OrderOutbox"))
    ///     .AddOutbox("payments", outbox => outbox
    ///         .WithPublisher&lt;PaymentProcessed, PaymentPublisher&gt;()
    ///         .UseSqlStore("Server=.;Database=MyApp;...", o => o.TableName = "PaymentOutbox"));
    /// </example>
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        string outboxName,
        Action<OutboxBuilder> configure)
    {
        PublisherRegistry registry;
        var existingRegistry = services.FirstOrDefault(d => d.ServiceType == typeof(PublisherRegistry));
        if (existingRegistry == null)
        {
            registry = new PublisherRegistry();
            services.AddSingleton(registry);
            services.AddScoped<IOutboxWriter, OutboxWriter>();
            services.AddScoped<ITransactionalOutboxWriter, TransactionalOutboxWriter>();
            services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        }
        else
        {
            registry = (PublisherRegistry)existingRegistry.ImplementationInstance!;
        }

        var storeResolver = EnsureStoreResolver(services);

        var optionsResolver = EnsureOptionsResolver(services);
        var builder = new OutboxBuilder(services, outboxName, registry, storeResolver, optionsResolver);
        configure(builder);
        return services;
    }

    private static OutboxOptionsResolver EnsureOptionsResolver(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(OutboxOptionsResolver));
        if (existing != null)
            return (OutboxOptionsResolver)existing.ImplementationInstance!;

        var resolver = new OutboxOptionsResolver();
        services.AddSingleton(resolver);
        return resolver;
    }

    private static OutboxStoreResolver EnsureStoreResolver(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(OutboxStoreResolver));
        if (existing != null)
            return (OutboxStoreResolver)existing.ImplementationInstance!;

        var resolver = new OutboxStoreResolver();
        services.AddSingleton(resolver);
        return resolver;
    }

    /// <summary>
    /// Use SQL Server as the global/default outbox store (raw ADO.NET, no ORM).
    /// All outboxes without a builder-level <c>.UseSqlStore()</c> will fall back to this store.
    /// </summary>
    public static IServiceCollection UseSqlOutboxStore(
        this IServiceCollection services,
        string connectionString,
        Action<SqlOutboxStoreOptions>? configure = null)
    {
        var opts = new SqlOutboxStoreOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var store = new SqlOutboxStore(opts);

        services.AddSingleton(opts);
        services.AddSingleton<IOutboxStore>(store);

        var resolver = EnsureStoreResolver(services);
        resolver.Register("*", store);

        if (opts.AutoMigrateSchema)
            EnsureSchemaInitializer(services);

        return services;
    }

    /// <summary>
    /// Use in-memory store as the global default — for tests and local development.
    /// Registered as singleton so test classes can inject it directly to inspect state.
    /// </summary>
    public static IServiceCollection UseInMemoryOutboxStore(this IServiceCollection services)
    {
        var store = new InMemoryOutboxStore();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton(store);

        var resolver = EnsureStoreResolver(services);
        resolver.Register("*", store);

        return services;
    }

    private static void EnsureSchemaInitializer(IServiceCollection services) =>
        EnsureSchemaInitializerInternal(services);

    internal static void EnsureSchemaInitializerInternal(IServiceCollection services)
    {
        if (!services.Any(d => d.ServiceType == typeof(IHostedService)
                             && d.ImplementationType == typeof(OutboxSchemaInitializer)))
        {
            services.AddHostedService<OutboxSchemaInitializer>();
        }
    }
}

// -------------------------------------------------------------------------
// Fluent builder
// -------------------------------------------------------------------------

public sealed class OutboxBuilder
{
    private readonly IServiceCollection services;
    private readonly string outboxName;
    private readonly PublisherRegistry registry;
    private readonly OutboxStoreResolver storeResolver;
    private readonly OutboxOptionsResolver optionsResolver;

    private readonly OutboxOptions _options = new();

    internal OutboxBuilder(IServiceCollection services, string outboxName, PublisherRegistry registry, OutboxStoreResolver storeResolver, OutboxOptionsResolver optionsResolver)
    {
        this.services = services;
        this.outboxName = outboxName;
        this.registry = registry;
        this.storeResolver = storeResolver;
        this.optionsResolver = optionsResolver;

        // Register the options instance now; hook methods mutate the same reference,
        // so DI will always see the fully-configured object.
        services.AddSingleton(_options);
        optionsResolver.Register(outboxName, _options);
    }

    /// <summary>
    /// Use a dedicated SQL Server store for this outbox (separate table).
    /// Overrides the global store registered via <c>UseSqlOutboxStore()</c> for this outbox only.
    /// </summary>
    public OutboxBuilder UseSqlStore(string connectionString, Action<SqlOutboxStoreOptions>? configure = null)
    {
        var opts = new SqlOutboxStoreOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        var store = new SqlOutboxStore(opts);
        storeResolver.Register(outboxName, store);

        if (opts.AutoMigrateSchema)
            ServiceCollectionExtensions.EnsureSchemaInitializerInternal(services);

        return this;
    }

    /// <summary>
    /// Use a dedicated in-memory store for this outbox.
    /// Useful for testing individual outboxes in isolation.
    /// </summary>
    public OutboxBuilder UseInMemoryStore()
    {
        var store = new InMemoryOutboxStore();
        storeResolver.Register(outboxName, store);
        return this;
    }

    /// <summary>
    /// Use a custom <see cref="IOutboxStore"/> implementation for this outbox.
    /// Overrides the global store for this outbox only.
    /// The store's <c>EnsureSchemaAsync</c> will be called automatically on startup.
    /// </summary>
    public OutboxBuilder UseStore(IOutboxStore store)
    {
        storeResolver.Register(outboxName, store);
        ServiceCollectionExtensions.EnsureSchemaInitializerInternal(services);
        return this;
    }

    /// <summary>Register a publisher for a message type in this outbox.</summary>
    public OutboxBuilder WithPublisher<TMessage, TPublisher>()
        where TPublisher : class, IOutboxPublisher<TMessage>
    {
        services.AddScoped<IOutboxPublisher<TMessage>, TPublisher>();

        registry.Register(outboxName, typeof(TMessage));

        return this;
    }

    public OutboxBuilder WithMaxRetries(int retries)
    {
        _options.MaxRetries = retries;
        return this;
    }

    public OutboxBuilder OnMessageStored(Func<OutboxMessage, Task> hook)
    {
        _options.OnMessageStored = hook;
        return this;
    }

    public OutboxBuilder OnPublished(Func<OutboxMessage, Task> hook)
    {
        _options.OnPublished = hook;
        return this;
    }

    public OutboxBuilder OnFailed(Func<OutboxMessage, Exception, Task> hook)
    {
        _options.OnFailed = hook;
        return this;
    }

    /// <summary>
    /// Called when a message is dead-lettered after exhausting retries.
    /// Fire alerts, push to a secondary queue, etc.
    /// </summary>
    public OutboxBuilder OnDeadLettered(Func<OutboxMessage, Exception, Task> hook)
    {
        _options.OnDeadLettered = hook;
        return this;
    }

    /// <summary>
    /// Enable automatic background dispatch for this outbox. Registers a
    /// <see cref="BackgroundService"/> that wakes immediately when a message
    /// is stored and also polls on a fallback interval.
    /// <para>
    /// This replaces the need for a hand-written dispatcher service and the
    /// <c>OnMessageStored</c> wake hook — both are wired internally.
    /// </para>
    /// </summary>
    /// <example>
    /// services.AddOutbox("orders", outbox => outbox
    ///     .WithPublisher&lt;OrderConfirmed, OrderPublisher&gt;()
    ///     .WithAutoDispatch(o => o.FallbackInterval = TimeSpan.FromSeconds(15)));
    /// </example>
    public OutboxBuilder WithAutoDispatch(Action<AutoDispatchOptions>? configure = null)
    {
        var dispatchOptions = new AutoDispatchOptions();
        configure?.Invoke(dispatchOptions);

        var signalRegistry = EnsureWakeSignalRegistry(services);
        var signal = signalRegistry.GetOrCreate(outboxName);

        // Wire OnMessageStored to wake the auto-dispatcher.
        // If the user already set OnMessageStored, chain the hooks.
        var existingHook = _options.OnMessageStored;
        _options.OnMessageStored = existingHook is null
            ? _ => { signal.Wake(); return Task.CompletedTask; }
            : async msg => { signal.Wake(); await existingHook(msg); };

        // Register the background dispatcher for this outbox.
        services.AddSingleton<IHostedService>(sp => new Internal.OutboxAutoDispatcherService(
            outboxName,
            signal,
            dispatchOptions,
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<Internal.OutboxAutoDispatcherService>()));

        return this;
    }

    private static Internal.OutboxWakeSignalRegistry EnsureWakeSignalRegistry(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(Internal.OutboxWakeSignalRegistry));
        if (existing != null)
            return (Internal.OutboxWakeSignalRegistry)existing.ImplementationInstance!;

        var registry = new Internal.OutboxWakeSignalRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}

// -------------------------------------------------------------------------
// Schema initializer
// -------------------------------------------------------------------------

internal sealed class OutboxSchemaInitializer(OutboxStoreResolver storeResolver, ILogger<OutboxSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        var stores = storeResolver.GetAll();
        logger.LogInformation("Ensuring outbox schema exists for {Count} store(s)...", stores.Count);
        foreach (var store in stores)
            await store.EnsureSchemaAsync(ct);
        logger.LogInformation("Outbox schema ready.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
