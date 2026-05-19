using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;

namespace Relay.Outbox.Internal;

/// <summary>
/// Built-in background dispatcher for a named outbox. Wakes immediately via
/// <see cref="OutboxWakeSignal"/> when a message is stored, and polls on a
/// fallback interval to catch edge cases (app restart, missed signals).
/// </summary>
internal sealed class OutboxAutoDispatcherService(
    string outboxName,
    OutboxWakeSignal wakeSignal,
    AutoDispatchOptions options,
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxAutoDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Auto-dispatcher started for outbox '{Outbox}'.", outboxName);

        while (!stoppingToken.IsCancellationRequested)
        {
            await wakeSignal.WaitAsync(options.FallbackInterval, stoppingToken);

            if (stoppingToken.IsCancellationRequested)
                break;

            try
            {
                using var scope = scopeFactory.CreateScope();
                var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
                var result = await dispatcher.DispatchPendingAsync(outboxName, options.BatchSize, stoppingToken);

                if (result.Published > 0)
                    logger.LogInformation(
                        "Auto-dispatch [{Outbox}] — published: {Published}.",
                        outboxName, result.Published);

                if (result.HasFailures)
                    logger.LogWarning(
                        "Auto-dispatch [{Outbox}] — failures: {Failed}.",
                        outboxName, result.Failed);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auto-dispatch [{Outbox}] failed.", outboxName);
            }
        }
    }
}
