using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Sample.ManualInbox.Domain;

namespace Relay.Sample.ManualInbox.Functions;

/// <summary>
/// HTTP Triggers — the system boundary for incoming payment events.
///
/// Replaces the Minimal API POST /payments, POST /refunds, POST /demo/seed,
/// GET /stats endpoints from the original ManualInbox sample.
///
/// Uses IInboxClient (raw / client mode) — no IInboxHandler&lt;T&gt; registrations.
/// Azure Functions creates a new DI scope per invocation.
/// </summary>
public sealed class PaymentsFunction(
    IInboxClient inbox,
    IInboxStore inboxStore,
    ILogger<PaymentsFunction> logger)
{
    // POST /api/payments
    [Function("ReceivePayment")]
    public async Task<HttpResponseData> ReceivePayment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "payments")] HttpRequestData req,
        CancellationToken ct)
    {
        PaymentReceived? payment;
        try
        {
            payment = await req.ReadFromJsonAsync<PaymentReceived>(ct);
        }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body — expected PaymentReceived", ct);
            return bad;
        }

        if (payment is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Request body is required", ct);
            return bad;
        }

        logger.LogInformation("Receiving payment {PaymentId} from {CustomerId}",
            payment.PaymentId, payment.CustomerId);

        var result = await inbox.ReceiveAsync(
            inboxName: "payments",
            message: payment,
            idempotencyKey: $"payment:{payment.PaymentId}",
            source: "azure-functions-http",
            ct: ct);

        var statusCode = result.WasDuplicate ? HttpStatusCode.OK : HttpStatusCode.Accepted;
        var response = req.CreateResponse(statusCode);

        await response.WriteAsJsonAsync(new
        {
            status = result.WasDuplicate ? "duplicate" : "accepted",
            paymentId = payment.PaymentId,
            messageId = result.MessageId,
        }, ct);

        return response;
    }

    // POST /api/refunds
    [Function("ReceiveRefund")]
    public async Task<HttpResponseData> ReceiveRefund(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "refunds")] HttpRequestData req,
        CancellationToken ct)
    {
        RefundIssued? refund;
        try
        {
            refund = await req.ReadFromJsonAsync<RefundIssued>(ct);
        }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body — expected RefundIssued", ct);
            return bad;
        }

        if (refund is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Request body is required", ct);
            return bad;
        }

        logger.LogInformation("Receiving refund {RefundId} for payment {OriginalPaymentId}",
            refund.RefundId, refund.OriginalPaymentId);

        var result = await inbox.ReceiveAsync(
            inboxName: "payments",
            message: refund,
            idempotencyKey: $"refund:{refund.RefundId}",
            source: "azure-functions-http",
            ct: ct);

        var statusCode = result.WasDuplicate ? HttpStatusCode.OK : HttpStatusCode.Accepted;
        var response = req.CreateResponse(statusCode);

        await response.WriteAsJsonAsync(new
        {
            status = result.WasDuplicate ? "duplicate" : "accepted",
            refundId = refund.RefundId,
            messageId = result.MessageId,
        }, ct);

        return response;
    }

    // GET /api/stats
    [Function("GetStats")]
    public async Task<HttpResponseData> GetStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "stats")] HttpRequestData req,
        CancellationToken ct)
    {
        var stats = await inboxStore.GetStatsAsync("payments", ct);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(stats, ct);
        return response;
    }

    // GET /api/payments/dead
    [Function("GetDeadLettered")]
    public async Task<HttpResponseData> GetDeadLettered(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "payments/dead")] HttpRequestData req,
        CancellationToken ct)
    {
        var dead = await inboxStore.GetDeadLetteredAsync("payments", ct: ct);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(dead, ct);
        return response;
    }

    // POST /api/payments/{id}/requeue
    [Function("RequeuePayment")]
    public async Task<HttpResponseData> RequeuePayment(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "payments/{id}/requeue")] HttpRequestData req,
        Guid id,
        CancellationToken ct)
    {
        var requeued = await inboxStore.RequeueAsync(id, ct);

        var response = req.CreateResponse(requeued ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(requeued
            ? new { requeued = true, messageId = id }
            : (object)new { error = "Message not found or not in a requeueable state." }, ct);

        return response;
    }

    // POST /api/demo/seed  — convenience endpoint for local testing
    [Function("SeedPayments")]
    public async Task<HttpResponseData> SeedPayments(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/seed")] HttpRequestData req,
        CancellationToken ct)
    {
        object[] events =
        [
            new PaymentReceived("PAY-001", "CUST-A", 49.99m,  "USD"),
            new PaymentReceived("PAY-002", "CUST-B", 120.00m, "EUR"),
            new RefundIssued   ("REF-001", "PAY-001", 49.99m,  "USD"),
        ];

        var results = new List<object>();
        foreach (var evt in events)
        {
            var (key, label) = evt switch
            {
                PaymentReceived p => ($"payment:{p.PaymentId}", p.PaymentId),
                RefundIssued rf => ($"refund:{rf.RefundId}", rf.RefundId),
                _ => throw new InvalidOperationException()
            };

            var received = await inbox.ReceiveAsync("payments", evt, key, source: "seed", ct: ct);
            results.Add(new
            {
                id = label,
                type = evt.GetType().Name,
                status = received.WasDuplicate ? "duplicate" : "accepted",
                messageId = received.MessageId,
            });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(results, ct);
        return response;
    }
}
