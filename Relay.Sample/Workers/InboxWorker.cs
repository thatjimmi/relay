using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;

namespace Relay.Sample.Workers;

/// <summary>
/// Periodically sweeps the inbox for pending/failed messages and processes them.
/// Creates a fresh DI scope per tick so scoped services (handlers, etc.) are
/// properly disposed after each batch.
/// </summary>
public sealed class InboxWorker(
    IServiceProvider sp,
    IConfiguration config,
    ILogger<InboxWorker> logger) : BackgroundService
{
    private const string Channel = "orders";

    private readonly TimeSpan _interval = TimeSpan.FromSeconds(
        config.GetValue("Relay:Inbox:PollingIntervalSeconds", 5));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        logger.LogInformation(
            "Inbox worker started — polling every {Interval}s on channel '{Channel}'",
            _interval.TotalSeconds, Channel);

        using var timer = new PeriodicTimer(_interval);

        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = sp.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IInboxProcessor>();
                var result = await processor.ProcessPendingAsync(Channel, ct: ct);

                if (result.Total > 0)
                    logger.LogInformation(
                        "Inbox sweep — processed: {Processed}, failed: {Failed}",
                        result.Processed, result.Failed);

                foreach (var failure in result.Failures)
                    logger.LogWarning(
                        "Inbox failure — id: {Id}, type: {Type}, deadLettered: {Dead}, error: {Error}",
                        failure.MessageId, failure.MessageType, failure.DeadLettered, failure.Error);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox worker encountered an unexpected error");
            }
        }

        logger.LogInformation("Inbox worker stopped");
    }
}
