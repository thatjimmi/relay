# Relay

A .NET 8 library that implements the **Transactional Inbox** and **Transactional Outbox** patterns with a clean, scheduler-agnostic, transport-agnostic design.

---

## Why Relay?

Distributed systems face two classic problems:

- **Duplicate processing** — the same incoming message processed more than once.
- **Lost outgoing events** — a message published before the database commits, then the commit fails.

Relay solves both:

- The **inbox** deduplicates and persists incoming messages before processing them.
- The **outbox** stages outgoing messages in the same database transaction as your business logic, then dispatches them reliably in a separate step.

No message broker required. No opinionated background threads. Bring your own scheduler.

---

## Features

- Idempotent inbox — duplicate messages are detected and skipped
- Atomic outbox — stage outgoing events in the same unit of work as your handler
- Retry + dead-letter with configurable max attempts and hooks
- Scheduled dispatch — stage a message for future delivery
- Inbox-to-outbox correlation — trace cause and effect across the boundary
- SKIP LOCKED support for safe concurrent processing across multiple instances
- Pluggable storage — SQL Server / Azure SQL built-in; implement `IInboxStore` / `IOutboxStore` for anything else
- In-memory stores for fast, zero-setup unit tests
- Dedicated testing packages with pre-built fakes

---

## Project Structure

```
Relay/
├── Relay/                        # Meta-package with fluent AddRelay() builder
├── Relay.Inbox/                  # Inbox core (receiver, processor, SQL + in-memory stores)
├── Relay.Outbox/                 # Outbox core (writer, dispatcher, SQL + in-memory stores)
├── Relay.Inbox.Testing/          # FakeInboxHandler for unit tests
├── Relay.Outbox.Testing/         # FakeOutboxPublisher, AlwaysFailingPublisher, FailThenSucceedPublisher
├── Relay.Inbox.Tests/            # Inbox unit tests (xUnit)
├── Relay.Outbox.Tests/           # Outbox unit tests (xUnit)
├── Relay.Tests/                  # Inbox + outbox integration tests
├── Relay.Sample/                 # ASP.NET Core minimal API sample (SQLite)
└── Relay.Sample.AzureFunctions/  # Azure Functions (Isolated Worker) sample (SQLite)
```

---

## Quick Start

### 1. Register services

```csharp
builder.Services.AddRelay(relay => relay
    .AddChannel("orders", channel => channel
        .Inbox(inbox => inbox
            .WithHandler<OrderPlaced, OrderPlacedHandler>()
            .WithMaxRetries(5))
        .Outbox(outbox => outbox
            .WithPublisher<OrderFulfillmentRequested, FulfillmentPublisher>()
            .WithPublisher<OrderConfirmationSent, ConfirmationPublisher>()
            .OnDeadLettered((msg, ex) =>
            {
                // alert on-call, push to secondary queue, etc.
                return Task.CompletedTask;
            })))
    .UseSqlStore(connectionString));
```

Or register inbox and outbox independently:

```csharp
builder.Services.AddInbox("orders", inbox => inbox
    .WithHandler<OrderPlaced, OrderPlacedHandler>());

builder.Services.AddOutbox("orders", outbox => outbox
    .WithPublisher<OrderFulfillmentRequested, FulfillmentPublisher>());

builder.Services.UseSqlInboxStore(connectionString);
builder.Services.UseSqlOutboxStore(connectionString);
```

### 2. Implement a handler

```csharp
public class OrderPlacedHandler(IOutboxWriter outbox) : IInboxHandler<OrderPlaced>
{
    // Derive a stable key from the message data — same logical message must always return the same key.
    public string GetIdempotencyKey(OrderPlaced msg) => $"order-placed:{msg.OrderId}";

    public async Task HandleAsync(OrderPlaced msg, CancellationToken ct = default)
    {
        // Stage outgoing messages atomically with your business logic.
        await outbox.WriteAsync(
            new OrderFulfillmentRequested(msg.OrderId, msg.CustomerId, msg.Items),
            outboxName: "orders",
            destination: "warehouse.fulfil",
            ct: ct);

        await outbox.WriteAsync(
            new OrderConfirmationSent(msg.OrderId, msg.CustomerId, msg.Total),
            outboxName: "orders",
            destination: "notifications.email",
            ct: ct);
    }
}
```

### 3. Implement a publisher

```csharp
public class FulfillmentPublisher : IOutboxPublisher<OrderFulfillmentRequested>
{
    public async Task PublishAsync(
        OrderFulfillmentRequested message,
        OutboxMessage envelope,
        CancellationToken ct = default)
    {
        // Send to RabbitMQ, Azure Service Bus, HTTP — anything.
        await bus.PublishAsync(message, ct);
    }
}
```

### 4. Receive messages at the system boundary

```csharp
app.MapPost("/orders", async (
    OrderPlaced order,
    IInboxReceiver<OrderPlaced> receiver,
    CancellationToken ct) =>
{
    var result = await receiver.ReceiveAsync(order, source: "api", ct: ct);

    return result.WasDuplicate
        ? Results.Ok(new { status = "duplicate" })
        : Results.Accepted($"/orders/{order.OrderId}", new { messageId = result.MessageId });
});
```

### 5. Drive processing from your scheduler

Relay has no built-in background threads. Call the processor and dispatcher from wherever fits your stack:

**ASP.NET Core BackgroundService**

```csharp
public class InboxWorker(IInboxProcessor processor) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
            await processor.ProcessPendingAsync("orders", batchSize: 50, ct);
    }
}
```

**Azure Functions timer trigger**

```csharp
[Function("ProcessInbox")]
public async Task Run([TimerTrigger("*/10 * * * * *")] TimerInfo timer, CancellationToken ct)
    => await processor.ProcessPendingAsync("orders", batchSize: 50, ct);

[Function("DispatchOutbox")]
public async Task Run([TimerTrigger("*/10 * * * * *")] TimerInfo timer, CancellationToken ct)
    => await dispatcher.DispatchPendingAsync("orders", batchSize: 50, ct);
```

---

## Inbox-to-Outbox Correlation

Set a correlation scope inside your inbox options to automatically tag outbox messages with the inbox message ID that caused them:

```csharp
builder.Services.AddInbox("orders", inbox => inbox
    .WithOptions(o =>
    {
        o.CorrelationScope = OutboxCorrelationContext.Set;
    }));
```

Then query across the boundary:

```csharp
var linked = await outboxStore.GetByCorrelationIdAsync(inboxMessageId);
```

---

## Scheduled Dispatch

Stage a message for future delivery:

```csharp
await outbox.WriteScheduledAsync(
    new SendReminderEmail(order.CustomerId),
    outboxName: "orders",
    scheduledFor: DateTime.UtcNow.AddHours(24));
```

The message will not appear in the pending batch until its scheduled time.

---

## Storage Options

| Store                  | Package                                  | Notes                                                                 |
| ---------------------- | ---------------------------------------- | --------------------------------------------------------------------- |
| SQL Server / Azure SQL | `Relay.Inbox`, `Relay.Outbox`            | SKIP LOCKED for concurrent instances; auto-migrates schema on startup |
| In-memory              | `Relay.Inbox`, `Relay.Outbox`            | Zero setup; ideal for tests and local dev                             |
| Custom                 | Implement `IInboxStore` / `IOutboxStore` | EF Core, MongoDB, Redis — anything                                    |

**SQL store options:**

```csharp
services.UseSqlInboxStore(connectionString, o =>
{
    o.TableName         = "InboxMessages"; // default
    o.AutoMigrateSchema = true;            // set false to manage via migrations
    o.UseSkipLocked     = true;            // requires SQL Server 2019+ or Azure SQL
});
```

### Per-Inbox / Per-Outbox Store Isolation

By default, all inboxes share one table and all outboxes share one table. When you need **physical isolation** — separate tables, databases, or even storage engines per inbox/outbox — register a store at the builder level:

**SQL Server — each inbox/outbox with its own table:**

```csharp
builder.Services
    .AddInbox("orders", inbox => inbox
        .WithHandler<OrderPlaced, OrderPlacedHandler>()
        .UseSqlStore(connectionString, o => o.TableName = "OrderInbox"))
    .AddInbox("payments", inbox => inbox
        .WithHandler<PaymentReceived, PaymentHandler>()
        .UseSqlStore(connectionString, o => o.TableName = "PaymentInbox"));

builder.Services
    .AddOutbox("orders", outbox => outbox
        .WithPublisher<OrderConfirmed, OrderPublisher>()
        .UseSqlStore(connectionString, o => o.TableName = "OrderOutbox"))
    .AddOutbox("payments", outbox => outbox
        .WithPublisher<PaymentProcessed, PaymentPublisher>()
        .UseSqlStore(connectionString, o => o.TableName = "PaymentOutbox"));
```

**Custom store — use any `IInboxStore` / `IOutboxStore` implementation:**

```csharp
builder.Services.AddInbox("orders", inbox => inbox
    .WithHandler<OrderPlaced, OrderPlacedHandler>()
    .UseStore(new SqliteInboxStore(connStr, "OrderInboxMessages")));

builder.Services.AddOutbox("orders", outbox => outbox
    .WithPublisher<OrderConfirmed, OrderPublisher>()
    .UseStore(new SqliteOutboxStore(connStr, "OrderOutboxMessages")));
```

**Mixed mode — global default + per-inbox overrides:**

```csharp
// Global default for all inboxes/outboxes
builder.Services.UseSqlInboxStore(connectionString);
builder.Services.UseSqlOutboxStore(connectionString);

// Override just the payments inbox with its own table
builder.Services.AddInbox("payments", inbox => inbox
    .WithHandler<PaymentReceived, PaymentHandler>()
    .UseSqlStore(connectionString, o => o.TableName = "PaymentInbox"));

// The "orders" inbox continues to use the global default table
builder.Services.AddInbox("orders", inbox => inbox
    .WithHandler<OrderPlaced, OrderPlacedHandler>());
```

Schema initialisation runs automatically on startup for all registered stores.

---

## Dead Letters and Requeue

Messages that fail after exhausting retries are dead-lettered. Inspect and requeue them:

```csharp
// Inspect dead letters
var dead = await inboxStore.GetDeadLetteredAsync("orders");

// Requeue a single message
await inboxStore.RequeueAsync(messageId);

// Requeue all dead letters for a channel
await inboxStore.RequeueAllDeadLetteredAsync("orders");
```

Hook into lifecycle events:

```csharp
inbox.WithOptions(o =>
{
    o.OnProcessed    = msg => metrics.IncrementAsync("inbox.processed");
    o.OnFailed       = (msg, ex) => logger.LogWarning(ex, "Inbox message {Id} failed", msg.Id);
    o.OnDeadLettered = (msg, ex) => alerts.FireAsync($"Dead letter: {msg.IdempotencyKey}");
    o.OnDuplicate    = key => logger.LogDebug("Duplicate ignored: {Key}", key);
});
```

---

## Testing

Use in-memory stores and the provided test doubles:

```csharp
// Outbox
var store     = new InMemoryOutboxStore();
var writer    = new OutboxWriter(store);
var publisher = new FakeOutboxPublisher<OrderFulfillmentRequested>();

// ... call your handler, then assert:
Assert.Single(publisher.Messages);
Assert.Equal("ORD-001", publisher.Messages[0].OrderId);

// Test retry paths
var failing = new AlwaysFailingPublisher<OrderFulfillmentRequested>();

// Test partial failure
var flaky = new FailThenSucceedPublisher<OrderFulfillmentRequested>(failTimes: 2);
```

```csharp
// Inbox
var store    = new InMemoryInboxStore();
var handler  = new FakeInboxHandler<OrderPlaced>(msg => $"order:{msg.OrderId}");
var receiver = new InboxReceiver<OrderPlaced>(store, handler, ...);

var result = await receiver.ReceiveAsync(order);
Assert.False(result.WasDuplicate);

// Second call with same order — duplicate detected
var dup = await receiver.ReceiveAsync(order);
Assert.True(dup.WasDuplicate);
```

---

## Requirements

- .NET 8
- SQL Server 2019+ or Azure SQL (when using the SQL store with SKIP LOCKED)
- Any .NET-compatible message broker or HTTP transport (for publishers)

---

## License

MIT
