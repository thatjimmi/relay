using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Sample.ManualInbox.Domain;

namespace Relay.Sample.ManualInbox.Workers;

/// <summary>
/// Periodically sweeps the "payments" inbox and processes messages manually —
/// no IInboxHandler&lt;T&gt; implementations anywhere in this project.
///
/// IInboxClient.GetPendingAsync fetches the raw messages; processing logic
/// switches on msg.Type to deserialize and handle each event variant.
/// </summary>
public sealed class InboxWorker(
    IServiceProvider sp,
    IConfiguration config,
    ILogger<InboxWorker> logger) : BackgroundService
{
    private const string Channel = "payments";

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
                var inbox = scope.ServiceProvider.GetRequiredService<IInboxClient>();

                var messages = await inbox.GetPendingAsync(Channel, batchSize: 50, ct);

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
                    }
                    catch (Exception ex)
                    {
                        await inbox.MarkFailedAsync(msg, ex.Message, maxRetries: 5, ct);
                    }
                }

                if (messages.Count > 0)
                    logger.LogInformation("Inbox sweep — processed {Count} message(s)", messages.Count);
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
