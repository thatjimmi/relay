using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;

namespace Relay.Sample.Workers;

/// <summary>
/// Periodically sweeps the outbox for pending/failed messages and dispatches them
/// to their registered publishers.
/// Creates a fresh DI scope per tick so scoped services are properly disposed.
/// </summary>
public sealed class OutboxWorker(
    IServiceProvider       sp,
    IConfiguration         config,
    ILogger<OutboxWorker>  logger) : BackgroundService
{
    private const string Channel = "orders";

    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        config.GetValue("Relay:Outbox:PollingIntervalSeconds", 5));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "Outbox worker started — polling every {Interval}s on channel '{Channel}'",
            _interval.TotalSeconds, Channel);

        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = sp.CreateAsyncScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                var result = await dispatcher.DispatchPendingAsync(Channel, ct: ct);

                if (result.Total > 0)
                    logger.LogInformation(
                        "Outbox sweep — published: {Published}, failed: {Failed}",
                        result.Published, result.Failed);

                foreach (var failure in result.Failures)
                    logger.LogWarning(
                        "Outbox failure — id: {Id}, type: {Type}, deadLettered: {Dead}, error: {Error}",
                        failure.MessageId, failure.MessageType, failure.DeadLettered, failure.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox worker encountered an unexpected error");
            }
        }

        logger.LogInformation("Outbox worker stopped");
    }
}
