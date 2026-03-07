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
    /// builder.Services
    ///     .AddOutbox("market-exchange", outbox => outbox
    ///         .WithPublisher&lt;TradeConfirmedEvent, RabbitMqTradePublisher&gt;()
    ///         .WithPublisher&lt;PositionUpdatedEvent, RabbitMqPositionPublisher&gt;()
    ///         .OnDeadLettered((msg, ex) => alerts.NotifyAsync(msg)))
    ///     .UseSqlOutboxStore("Server=.;Database=MyApp;...");
    /// </example>
    public static IServiceCollection AddOutbox(
        this IServiceCollection services,
        string outboxName,
        Action<OutboxBuilder> configure)
    {
        PublisherRegistry registry;
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(PublisherRegistry));
        if (existing == null)
        {
            registry = new PublisherRegistry();
            services.AddSingleton(registry);
            services.AddScoped<IOutboxWriter, OutboxWriter>();
            services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();
        }
        else
        {
            registry = (PublisherRegistry)existing.ImplementationInstance!;
        }

        var builder = new OutboxBuilder(services, outboxName, registry);
        configure(builder);
        return services;
    }

    /// <summary>Use SQL Server as the outbox store (raw ADO.NET, no ORM).</summary>
    public static IServiceCollection UseSqlOutboxStore(
        this IServiceCollection services,
        string connectionString,
        Action<SqlOutboxStoreOptions>? configure = null)
    {
        var opts = new SqlOutboxStoreOptions { ConnectionString = connectionString };
        configure?.Invoke(opts);

        services.AddSingleton(opts);
        services.AddSingleton<IOutboxStore, SqlOutboxStore>();

        if (opts.AutoMigrateSchema)
            services.AddHostedService<OutboxSchemaInitializer>();

        return services;
    }

    /// <summary>
    /// Use in-memory store — for tests and local development.
    /// Registered as singleton so test classes can inject it directly to inspect state.
    /// </summary>
    public static IServiceCollection UseInMemoryOutboxStore(this IServiceCollection services)
    {
        var store = new InMemoryOutboxStore();
        services.AddSingleton<IOutboxStore>(store);
        services.AddSingleton(store);
        return services;
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

    private readonly OutboxOptions _options = new();

    internal OutboxBuilder(IServiceCollection services, string outboxName, PublisherRegistry registry)
    {
        this.services = services;
        this.outboxName = outboxName;
        this.registry = registry;
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
}

// -------------------------------------------------------------------------
// Schema initializer
// -------------------------------------------------------------------------

internal sealed class OutboxSchemaInitializer(IOutboxStore store, ILogger<OutboxSchemaInitializer> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("Ensuring outbox schema exists...");
        await store.EnsureSchemaAsync(ct);
        logger.LogInformation("Outbox schema ready.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
