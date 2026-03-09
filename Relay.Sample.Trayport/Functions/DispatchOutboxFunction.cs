using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;

namespace Relay.Sample.Trayport.Functions;

/// <summary>
/// Timer Trigger — dispatches pending outbox messages to their publishers.
///
/// Runs slightly after ProcessTrades so that outbox messages staged during
/// trade processing are picked up in the same scheduling cycle.
///
/// IOutboxDispatcher is scoped; Azure Functions creates a new scope per invocation.
/// </summary>
public sealed class DispatchOutboxFunction(
    IOutboxDispatcher dispatcher,
    ILogger<DispatchOutboxFunction> logger)
{
    private const string Schedule = "*/15 * * * * *";

    [Function("DispatchOutbox")]
    public async Task Run(
        [TimerTrigger(Schedule, RunOnStartup = true, UseMonitor = false)] TimerInfo timer,
        CancellationToken ct)
    {
        logger.LogInformation("DispatchOutbox triggered — past-due: {PastDue}", timer.IsPastDue);

        var result = await dispatcher.DispatchPendingAsync("trades", batchSize: 50, ct: ct);

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
