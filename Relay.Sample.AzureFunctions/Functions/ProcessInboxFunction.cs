using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;

namespace Relay.Sample.AzureFunctions.Functions;

/// <summary>
/// Timer Trigger — replaces InboxWorker (BackgroundService + PeriodicTimer).
///
/// Azure Functions schedules this on the NCRONTAB expression, so you get the
/// same polling behaviour without running a dedicated background thread.
/// In a consumption plan this also means you only pay for actual execution time.
///
/// IInboxProcessor is scoped; Azure Functions creates a new scope per invocation.
/// </summary>
public sealed class ProcessInboxFunction(
    IInboxProcessor           processor,
    ILogger<ProcessInboxFunction> logger)
{
    // Every 30 seconds (6-part NCRONTAB used by Azure Functions).
    // To make this configurable without redeploying: replace the string literal with
    // "%InboxCron%" and add  "InboxCron": "*/30 * * * * *"  to Application Settings.
    private const string Schedule = "*/30 * * * * *";

    [Function("ProcessInbox")]
    public async Task Run(
        [TimerTrigger(Schedule, RunOnStartup = true, UseMonitor = false)] TimerInfo timer,
        CancellationToken ct)
    {
        logger.LogInformation("ProcessInbox triggered — past-due: {PastDue}", timer.IsPastDue);

        var result = await processor.ProcessPendingAsync("orders", batchSize: 50, ct: ct);

        if (result.Total > 0)
            logger.LogInformation(
                "Inbox sweep — processed: {Processed}, failed: {Failed}",
                result.Processed, result.Failed);

        foreach (var failure in result.Failures)
            logger.LogWarning(
                "Inbox failure — id: {Id}, type: {Type}, deadLettered: {Dead}, error: {Error}",
                failure.MessageId, failure.MessageType, failure.DeadLettered, failure.Error);
    }
}
