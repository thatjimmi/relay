using Relay.Inbox.Core;
using Relay.Inbox.Extensions;
using Relay.Outbox.Core;
using Relay.Outbox.Extensions;
using Relay.Outbox.Internal;
using Relay.Sample.Domain;
using Relay.Sample.Infrastructure;
using Relay.Sample.Workers;

var builder = WebApplication.CreateBuilder(args);

var connStr = $"Data Source={builder.Configuration["Relay:Database"] ?? "relay-sample.db"}";

// ── Inbox ─────────────────────────────────────────────────────────────────
builder.Services
    .AddInbox("orders", inbox => inbox
        .WithHandler<OrderPlaced, OrderPlacedHandler>()
        .WithOptions(o =>
        {
            // Automatically sets a correlation context so that any outbox messages
            // written during handler execution carry the inbox message ID.
            o.CorrelationScope = OutboxCorrelationContext.Set;

            o.OnDeadLettered = (msg, ex) =>
            {
                // In production: fire a PagerDuty / Slack alert here.
                Console.Error.WriteLine(
                    $"[DEAD LETTER] inbox={msg.InboxName} key={msg.IdempotencyKey} error={ex.Message}");
                return Task.CompletedTask;
            };
        }));

// ── Outbox ────────────────────────────────────────────────────────────────
builder.Services
    .AddOutbox("orders", outbox => outbox
        .WithPublisher<OrderFulfillmentRequested, FulfillmentPublisher>()
        .WithPublisher<OrderConfirmationSent, ConfirmationPublisher>()
        .OnDeadLettered((msg, ex) =>
        {
            Console.Error.WriteLine(
                $"[DEAD LETTER] outbox={msg.OutboxName} type={msg.Type} error={ex.Message}");
            return Task.CompletedTask;
        }));

// ── Infrastructure ────────────────────────────────────────────────────────
// Registers IInboxStore + IOutboxStore (SQLite) and initialises the schema
// via a hosted service that runs before Kestrel accepts connections.
builder.Services.AddSqliteRelayStores(connStr);

// ── Background workers ────────────────────────────────────────────────────
// InboxWorker  – processes pending inbox messages on a PeriodicTimer.
// OutboxWorker – dispatches staged outbox messages on a PeriodicTimer.
// Polling intervals are read from appsettings.json → Relay:Inbox/Outbox:PollingIntervalSeconds
builder.Services.AddHostedService<InboxWorker>();
builder.Services.AddHostedService<OutboxWorker>();

// ── JSON serialisation ────────────────────────────────────────────────────
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.WriteIndented = true);

// ─────────────────────────────────────────────────────────────────────────

var app = builder.Build();

// ── Endpoints ─────────────────────────────────────────────────────────────

// POST /orders
// Receive a single order at the system boundary.
// Returns 202 Accepted with the message ID, or 200 OK if it's a duplicate.
app.MapPost("/orders", async (
    OrderPlaced order,
    IInboxReceiver<OrderPlaced> receiver,
    CancellationToken ct) =>
{
    var result = await receiver.ReceiveAsync(order, source: "api", ct: ct);

    return result.WasDuplicate
        ? Results.Ok(new { status = "duplicate", orderId = order.OrderId })
        : Results.Accepted(
            $"/orders/{order.OrderId}",
            new { status = "accepted", orderId = order.OrderId, messageId = result.MessageId });
});

// GET /stats
// Current inbox and outbox stats for the "orders" channel.
app.MapGet("/stats", async (
    IInboxStore  inboxStore,
    IOutboxStore outboxStore,
    CancellationToken ct) =>
{
    var inbox  = await inboxStore.GetStatsAsync("orders", ct);
    var outbox = await outboxStore.GetStatsAsync("orders", ct);
    return Results.Ok(new { inbox, outbox });
});

// GET /orders/dead
// Inspect dead-lettered inbox messages (failed after exhausting all retries).
app.MapGet("/orders/dead", async (
    IInboxStore inboxStore,
    CancellationToken ct) =>
{
    var dead = await inboxStore.GetDeadLetteredAsync("orders", ct: ct);
    return Results.Ok(dead);
});

// POST /orders/{id}/requeue
// Requeue a single dead-lettered or failed inbox message for reprocessing.
app.MapPost("/orders/{id}/requeue", async (
    Guid id,
    IInboxStore inboxStore,
    CancellationToken ct) =>
{
    var requeued = await inboxStore.RequeueAsync(id, ct);
    return requeued
        ? Results.Ok(new { requeued = true, messageId = id })
        : Results.NotFound(new { error = "Message not found or not in a requeueable state." });
});

// POST /demo/seed
// Convenience endpoint — seeds a few sample orders so you can watch the
// workers pick them up without writing curl commands by hand.
app.MapPost("/demo/seed", async (
    IInboxReceiver<OrderPlaced> receiver,
    CancellationToken ct) =>
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
        var r = await receiver.ReceiveAsync(o, source: "demo-seed", ct: ct);
        results.Add(new
        {
            orderId = o.OrderId,
            status  = r.WasDuplicate ? "duplicate" : "accepted",
            messageId = r.MessageId,
        });
    }

    return Results.Ok(results);
});

// ─────────────────────────────────────────────────────────────────────────

app.Logger.LogInformation("Relay Sample API started");
app.Logger.LogInformation("SQLite database: {Path}", Path.GetFullPath(
    builder.Configuration["Relay:Database"] ?? "relay-sample.db"));
app.Logger.LogInformation("Try: POST http://localhost:5000/demo/seed  then  GET http://localhost:5000/stats");

app.Run();
