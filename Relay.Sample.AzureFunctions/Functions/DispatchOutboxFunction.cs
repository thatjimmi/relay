using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;

namespace Relay.Sample.AzureFunctions.Functions;

/// <summary>
/// Timer Trigger — replaces OutboxWorker (BackgroundService + PeriodicTimer).
///
/// Runs slightly after ProcessInbox so that outbox messages staged during inbox
/// processing are picked up in the same scheduling cycle. In practice the offset
/// doesn't matter much — pending messages accumulate and are dispatched on the
/// next tick regardless.
///
/// IOutboxDispatcher is scoped; Azure Functions creates a new scope per invocation.
/// </summary>
public sealed class DispatchOutboxFunction(
    IOutboxDispatcher              dispatcher,
    ILogger<DispatchOutboxFunction> logger)
{
    // Every 30 seconds (6-part NCRONTAB used by Azure Functions).
    // To make this configurable without redeploying: replace the string literal with
    // "%OutboxCron%" and add  "OutboxCron": "*/30 * * * * *"  to Application Settings.
    private const string Schedule = "*/30 * * * * *";

    [Function("DispatchOutbox")]
    public async Task Run(
        [TimerTrigger(Schedule, RunOnStartup = true, UseMonitor = false)] TimerInfo timer,
        CancellationToken ct)
    {
        logger.LogInformation("DispatchOutbox triggered — past-due: {PastDue}", timer.IsPastDue);

        var result = await dispatcher.DispatchPendingAsync("orders", batchSize: 50, ct: ct);

        if (result.Total > 0)
            logger.LogInformation(
                "Outbox sweep — published: {Published}, failed: {Failed}",
                result.Published, result.Failed);

        foreach (var failure in result.Failures)
            logger.LogWarning(
                "Outbox failure — id: {Id}, type: {Type}, deadLettered: {Dead}, error: {Error}",
                failure.MessageId, failure.MessageType, failure.DeadLettered, failure.Error);
    }
}
