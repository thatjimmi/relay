using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Inbox.Extensions;
using Relay.Outbox.Core;
using Relay.Outbox.Extensions;
using Relay.Outbox.Internal;
using Relay.Sample.Domain;
using Relay.Sample.Storage;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------

const string DbPath = "relay-sample.db";
const string Channel = "orders";
var connStr = $"Data Source={DbPath}";

// ---------------------------------------------------------------------------
// DI setup
// ---------------------------------------------------------------------------

var services = new ServiceCollection();

services.AddLogging(b => b
    .AddConsole()
    .SetMinimumLevel(LogLevel.Warning)); // suppress framework noise

// Inbox: register the "orders" channel with its handler
services
    .AddInbox(Channel, inbox => inbox
        .WithHandler<OrderPlaced, OrderPlacedHandler>()
        .WithOptions(o =>
        {
            // Link inbox → outbox correlation automatically
            o.CorrelationScope = OutboxCorrelationContext.Set;
            o.OnProcessed = msg =>
            {
                Console.WriteLine($"  [inbox]  processed  {msg.IdempotencyKey}");
                return Task.CompletedTask;
            };
            o.OnDeadLettered = (msg, ex) =>
            {
                Console.WriteLine($"  [inbox]  DEAD       {msg.IdempotencyKey}: {ex.Message}");
                return Task.CompletedTask;
            };
        }));

// Outbox: register the "orders" channel with its publishers
services
    .AddOutbox(Channel, outbox => outbox
        .WithPublisher<OrderFulfillmentRequested, FulfillmentPublisher>()
        .WithPublisher<OrderConfirmationSent, ConfirmationPublisher>()
        .OnPublished(msg =>
        {
            var shortType = msg.Type.Split('.').Last();
            var shortCorr = msg.CorrelationId is { Length: > 8 } c ? c[..8] + "…" : msg.CorrelationId;
            Console.WriteLine($"  [outbox] published  {shortType,-35} corr: {shortCorr}");
            return Task.CompletedTask;
        }));

// Register SQLite stores (implement IInboxStore / IOutboxStore)
var inboxStore  = new SqliteInboxStore(connStr);
var outboxStore = new SqliteOutboxStore(connStr);
services.AddSingleton<IInboxStore>(inboxStore);
services.AddSingleton<IOutboxStore>(outboxStore);

var sp = services.BuildServiceProvider();

// ---------------------------------------------------------------------------
// Ensure schema
// ---------------------------------------------------------------------------

await inboxStore.EnsureSchemaAsync();
await outboxStore.EnsureSchemaAsync();

Console.WriteLine($"SQLite db: {Path.GetFullPath(DbPath)}");
Console.WriteLine();

// ---------------------------------------------------------------------------
// Step 1: Receive messages at the system boundary
// ---------------------------------------------------------------------------

Console.WriteLine("=== Step 1: Receiving orders ===");

await using (var scope = sp.CreateAsyncScope())
{
    var receiver = scope.ServiceProvider.GetRequiredService<IInboxReceiver<OrderPlaced>>();

    var orders = new[]
    {
        new OrderPlaced("ORD-001", "CUST-A", ["Widget", "Gadget"],               49.99m),
        new OrderPlaced("ORD-002", "CUST-B", ["Thingamajig"],                    12.50m),
        new OrderPlaced("ORD-003", "CUST-C", ["Doohickey", "Gizmo", "Whatsit"], 199.00m),
        // Duplicate — same OrderId → same idempotency key → silently dropped
        new OrderPlaced("ORD-001", "CUST-A", ["Widget", "Gadget"],               49.99m),
    };

    foreach (var order in orders)
    {
        var result = await receiver.ReceiveAsync(order, source: "checkout-service");
        var tag = result.WasDuplicate ? "DUPLICATE" : "accepted ";
        Console.WriteLine($"  order {order.OrderId}: {tag}");
    }
}

// ---------------------------------------------------------------------------
// Step 2: Process the inbox — handlers run, outbox messages are staged
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== Step 2: Processing inbox ===");

await using (var scope = sp.CreateAsyncScope())
{
    var processor = scope.ServiceProvider.GetRequiredService<IInboxProcessor>();
    var result = await processor.ProcessPendingAsync(Channel);
    Console.WriteLine($"  processed: {result.Processed}  failed: {result.Failed}");
}

// ---------------------------------------------------------------------------
// Step 3: Dispatch the outbox — publishers deliver to downstream services
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== Step 3: Dispatching outbox ===");

await using (var scope = sp.CreateAsyncScope())
{
    var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
    var result = await dispatcher.DispatchPendingAsync(Channel);
    Console.WriteLine($"  published: {result.Published}  failed: {result.Failed}");
}

// ---------------------------------------------------------------------------
// Step 4: Stats — inspect what's in the database
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== Step 4: Stats ===");

var inboxStats  = await inboxStore.GetStatsAsync(Channel);
var outboxStats = await outboxStore.GetStatsAsync(Channel);

Console.WriteLine(
    $"  Inbox  — pending: {inboxStats.Pending}  processed: {inboxStats.Processed}" +
    $"  failed: {inboxStats.Failed}  dead: {inboxStats.DeadLettered}");
Console.WriteLine(
    $"  Outbox — pending: {outboxStats.Pending}  published: {outboxStats.Published}" +
    $"  failed: {outboxStats.Failed}  dead: {outboxStats.DeadLettered}");

// ---------------------------------------------------------------------------
// Step 5: Correlation — link outbox messages back to their inbox message
// ---------------------------------------------------------------------------

Console.WriteLine();
Console.WriteLine("=== Step 5: Correlation trace ===");

// Grab a correlation ID (= inbox message Id) from the outbox table
var firstCorrId = await GetFirstCorrelationIdAsync(connStr, Channel);

if (firstCorrId is not null)
{
    var linked = await outboxStore.GetByCorrelationIdAsync(firstCorrId);
    Console.WriteLine($"  Outbox messages tied to inbox message {firstCorrId}:");
    foreach (var m in linked)
        Console.WriteLine($"    {m.Type.Split('.').Last(),-35} status: {m.Status}  dest: {m.Destination}");
}

Console.WriteLine();
Console.WriteLine($"Done. Inspect {DbPath} with any SQLite viewer.");

// ---------------------------------------------------------------------------
// Helper
// ---------------------------------------------------------------------------

static async Task<string?> GetFirstCorrelationIdAsync(string cs, string outboxName)
{
    await using var conn = new SqliteConnection(cs);
    await conn.OpenAsync();
    await using var cmd = new SqliteCommand(
        "SELECT CorrelationId FROM OutboxMessages WHERE OutboxName = @n AND CorrelationId IS NOT NULL LIMIT 1",
        conn);
    cmd.Parameters.AddWithValue("@n", outboxName);
    return await cmd.ExecuteScalarAsync() as string;
}
