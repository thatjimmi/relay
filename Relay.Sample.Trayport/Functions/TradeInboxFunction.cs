using System.Net;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Outbox.Core;
using Relay.Sample.Trayport.Domain;

namespace Relay.Sample.Trayport.Functions;

/// <summary>
/// HTTP Triggers — the system boundary for incoming Trayport trades.
///
/// Uses IInboxClient (raw / client mode) — no IInboxHandler&lt;T&gt; registrations.
/// Trades are received with source-timestamp support so that amendments
/// (same TradeId, newer TradeDate) replace the original payload.
///
/// Azure Functions creates a new DI scope per invocation.
/// </summary>
public sealed class TradeInboxFunction(
    IInboxClient inbox,
    IInboxStore inboxStore,
    IOutboxStore outboxStore,
    ILogger<TradeInboxFunction> logger)
{
    private const string Channel = "trades";

    // POST /api/trades
    [Function("ReceiveTrade")]
    public async Task<HttpResponseData> ReceiveTrade(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trades")] HttpRequestData req,
        CancellationToken ct)
    {
        TrayportTrade? trade;
        try
        {
            trade = await req.ReadFromJsonAsync<TrayportTrade>(ct);
        }
        catch
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Invalid JSON body — expected TrayportTrade", ct);
            return bad;
        }

        if (trade is null)
        {
            var bad = req.CreateResponse(HttpStatusCode.BadRequest);
            await bad.WriteStringAsync("Request body is required", ct);
            return bad;
        }

        logger.LogInformation(
            "Receiving trade {TradeId} {Direction} {Volume} {Unit} {Commodity} @ {Price} from {Counterparty}",
            trade.TradeId, trade.Direction, trade.Volume, trade.Unit, trade.Commodity,
            trade.Price, trade.Counterparty);

        // Use TradeId as source-id — amendments with a newer TradeDate replace the original.
        var result = await inbox.ReceiveAsync(
            inboxName: Channel,
            message: trade,
            idempotencyKey: $"trade:{trade.TradeId}",
            source: "trayport-api",
            sourceTimestamp: trade.TradeDate,
            ct: ct);

        var statusCode = result.WasDuplicate ? HttpStatusCode.OK : HttpStatusCode.Accepted;
        var response = req.CreateResponse(statusCode);

        await response.WriteAsJsonAsync(new
        {
            status = result.WasUpdated ? "updated" : result.WasDuplicate ? "duplicate" : "accepted",
            tradeId = trade.TradeId,
            messageId = result.MessageId,
        }, ct);

        return response;
    }

    // GET /api/trades/stats
    [Function("GetTradeStats")]
    public async Task<HttpResponseData> GetStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "trades/stats")] HttpRequestData req,
        CancellationToken ct)
    {
        var inboxStats = await inboxStore.GetStatsAsync(Channel, ct);
        var outboxStats = await outboxStore.GetStatsAsync(Channel, ct);

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new { inbox = inboxStats, outbox = outboxStats }, ct);
        return response;
    }

    // GET /api/trades/dead
    [Function("GetDeadTrades")]
    public async Task<HttpResponseData> GetDeadLettered(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "trades/dead")] HttpRequestData req,
        CancellationToken ct)
    {
        var dead = await inboxStore.GetDeadLetteredAsync(Channel, ct: ct);
        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(dead, ct);
        return response;
    }

    // POST /api/trades/{id}/requeue
    [Function("RequeueTrade")]
    public async Task<HttpResponseData> Requeue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "trades/{id}/requeue")] HttpRequestData req,
        string id,
        CancellationToken ct)
    {
        var requeued = await inboxStore.RequeueAsync(Guid.Parse(id), ct);

        var response = req.CreateResponse(requeued ? HttpStatusCode.OK : HttpStatusCode.NotFound);
        await response.WriteAsJsonAsync(requeued
            ? new { requeued = true, messageId = id }
            : (object)new { error = "Message not found or not in a requeueable state." }, ct);

        return response;
    }

    // POST /api/demo/seed — convenience endpoint for local testing
    [Function("SeedTrades")]
    public async Task<HttpResponseData> SeedTrades(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "demo/seed")] HttpRequestData req,
        CancellationToken ct)
    {
        var now = DateTime.UtcNow;

        TrayportTrade[] trades =
        [
            new("TRD-001", "Power",   "Shell Energy",     85.50m,  100m, "MWh", now.AddHours(-2), "Buy"),
            new("TRD-002", "Gas",     "BP Trading",      42.75m,  500m, "therm", now.AddHours(-1), "Sell"),
            new("TRD-003", "Carbon",  "Vitol SA",        78.20m, 1000m, "tCO2",  now,              "Buy"),
        ];

        var results = new List<object>();
        foreach (var trade in trades)
        {
            var r = await inbox.ReceiveAsync(
                Channel, trade, $"trade:{trade.TradeId}",
                source: "seed",
                sourceTimestamp: trade.TradeDate,
                ct: ct);

            results.Add(new
            {
                tradeId = trade.TradeId,
                status = r.WasUpdated ? "updated" : r.WasDuplicate ? "duplicate" : "accepted",
                messageId = r.MessageId,
            });
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(results, ct);
        return response;
    }
}
