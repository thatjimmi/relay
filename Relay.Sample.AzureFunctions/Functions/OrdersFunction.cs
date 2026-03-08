using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Sample.AzureFunctions.Domain;

namespace Relay.Sample.AzureFunctions.Functions;

/// <summary>
/// HTTP Trigger — the system boundary for incoming orders.
///
/// Replaces the  POST /orders  endpoint from Relay.Sample.
///
/// Azure Functions creates a new DI scope per invocation, so scoped services
/// (like IInboxReceiver) can be constructor-injected safely.
/// </summary>
public sealed class OrdersFunction(
    IInboxReceiver<OrderPlaced> receiver,
    ILogger<OrdersFunction> logger)
{
    // POST /api/orders
    [Function("ReceiveOrder")]
    public async Task<HttpResponseData> ReceiveOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req,
        CancellationToken ct)
    {
        OrderPlaced? order;
        try
        {
            order = await req.ReadFromJsonAsync<OrderPlaced>(ct);
        }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body — expected OrderPlaced", ct);
            return bad;
        }

        if (order is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Request body is required", ct);
            return bad;
        }

        logger.LogInformation("Receiving order {OrderId} from {CustomerId}", order.OrderId, order.CustomerId);

        var result = order.SourceTimestamp.HasValue
            ? await receiver.ReceiveAsync(order, source: "azure-functions-http", sourceTimestamp: order.SourceTimestamp.Value, ct: ct)
            : await receiver.ReceiveAsync(order, source: "azure-functions-http", ct: ct);

        var statusCode = result.WasDuplicate ? HttpStatusCode.OK : HttpStatusCode.Accepted;
        var response = req.CreateResponse(statusCode);

        await response.WriteAsJsonAsync(new
        {
            status = result.WasUpdated ? "updated" : result.WasDuplicate ? "duplicate" : "accepted",
            orderId = order.OrderId,
            messageId = result.MessageId,
        }, ct);

        return response;
    }

    // POST /api/orders/seed  — convenience endpoint for local testing
    [Function("SeedOrders")]
    public async Task<HttpResponseData> SeedOrders(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders/seed")] HttpRequestData req,
        CancellationToken ct)
    {
        OrderPlaced[] orders =
        [
            new("ORD-001", "CUST-A", ["Widget", "Gadget"],               49.99m),
            new("ORD-002", "CUST-B", ["Thingamajig"],                    12.50m),
            new("ORD-003", "CUST-C", ["Doohickey", "Gizmo", "Whatsit"], 199.00m),
        ];

        var results = new List<object>();
        foreach (var o in orders)
        {
            var r = await receiver.ReceiveAsync(o, source: "seed", ct: ct);
            results.Add(new { orderId = o.OrderId, status = r.WasDuplicate ? "duplicate" : "accepted" });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(results, ct);
        return response;
    }
}
