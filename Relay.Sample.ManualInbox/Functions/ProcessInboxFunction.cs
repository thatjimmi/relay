using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Sample.ManualInbox.Domain;

namespace Relay.Sample.ManualInbox.Functions;

/// <summary>
/// Timer Trigger — replaces InboxWorker (BackgroundService + PeriodicTimer).
///
/// Polls the "payments" inbox and processes messages manually — no IInboxHandler&lt;T&gt;
/// implementations anywhere in this project. Processing logic switches on msg.Type
/// to deserialize and handle each event variant.
///
/// In production this would be a Service Bus trigger (triggered when a message is
/// inserted into the inbox and an event is published to Service Bus). We use a timer
/// trigger here because we don't have a Service Bus instance.
///
/// IInboxClient is scoped; Azure Functions creates a new scope per invocation.
/// </summary>
public sealed class ProcessInboxFunction(
    IInboxClient inbox,
    ILogger<ProcessInboxFunction> logger)
{
    private const string Channel = "payments";

    // Every 30 seconds (6-part NCRONTAB used by Azure Functions).
    // In production with Service Bus: [ServiceBusTrigger("payments-inbox", ...)]
    private const string Schedule = "*/30 * * * * *";

    [Function("ProcessInbox")]
    public async Task Run(
        [TimerTrigger(Schedule, RunOnStartup = true, UseMonitor = false)] TimerInfo timer,
        CancellationToken ct)
    {
        logger.LogInformation(
            "ProcessInbox triggered — past-due: {PastDue}", timer.IsPastDue);

        var messages = await inbox.GetPendingAsync(Channel, batchSize: 50, ct);

        if (messages.Count == 0)
            return;

        var processed = 0;
        var failed = 0;

        foreach (var msg in messages)
        {
            try
            {
                switch (msg.Type)
                {
                    case nameof(PaymentReceived):
                        var payment = JsonSerializer.Deserialize<PaymentReceived>(msg.Payload)!;
                        logger.LogInformation(
                            "Payment received — id={Id} customer={Customer} amount={Amount} {Currency}",
                            payment.PaymentId, payment.CustomerId, payment.Amount, payment.Currency);
                        break;

                    case nameof(RefundIssued):
                        var refund = JsonSerializer.Deserialize<RefundIssued>(msg.Payload)!;
                        logger.LogInformation(
                            "Refund issued — id={Id} originalPayment={Original} amount={Amount} {Currency}",
                            refund.RefundId, refund.OriginalPaymentId, refund.Amount, refund.Currency);
                        break;

                    default:
                        logger.LogWarning("Unknown event type '{Type}' — skipping", msg.Type);
                        break;
                }

                await inbox.MarkProcessedAsync(msg, ct);
                processed++;
            }
            catch (Exception ex)
            {
                await inbox.MarkFailedAsync(msg, ex.Message, maxRetries: 5, ct);
                failed++;

                logger.LogWarning(
                    "Inbox failure — id: {Id}, type: {Type}, error: {Error}",
                    msg.Id, msg.Type, ex.Message);
            }
        }

        logger.LogInformation(
            "Inbox sweep — processed: {Processed}, failed: {Failed}",
            processed, failed);
    }
}
