using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Outbox.Core;

namespace Relay.Sample.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>
    /// Registers the SQLite-backed inbox and outbox stores and schedules
    /// schema initialisation to run before the app starts handling requests.
    /// </summary>
    public static IServiceCollection AddSqliteRelayStores(
        this IServiceCollection services,
        string connectionString)
    {
        var inbox  = new SqliteInboxStore(connectionString);
        var outbox = new SqliteOutboxStore(connectionString);

        services.AddSingleton<IInboxStore>(inbox);
        services.AddSingleton<IOutboxStore>(outbox);

        // Run CREATE TABLE IF NOT EXISTS before the first request arrives.
        services.AddHostedService(sp => new SqliteSchemaInitializer(
            inbox, outbox,
            sp.GetRequiredService<ILogger<SqliteSchemaInitializer>>()));

        return services;
    }
}

/// <summary>
/// Hosted service that ensures both SQLite tables exist on startup.
/// Runs before the Kestrel server begins accepting connections.
/// </summary>
internal sealed class SqliteSchemaInitializer(
    SqliteInboxStore  inbox,
    SqliteOutboxStore outbox,
    ILogger<SqliteSchemaInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        logger.LogInformation("Initialising SQLite schema…");
        await inbox.EnsureSchemaAsync(ct);
        await outbox.EnsureSchemaAsync(ct);
        logger.LogInformation("SQLite schema ready");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
