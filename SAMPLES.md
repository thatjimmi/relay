# Relay Samples

Three sample projects showing the inbox/outbox pattern across different hosting models and API styles.

| Project | Hosting | Inbox Mode |
|---|---|---|
| `Relay.Sample` | ASP.NET Core minimal API + `BackgroundService` workers | Handler (`IInboxHandler<T>`) |
| `Relay.Sample.AzureFunctions` | Azure Functions v4 isolated worker (.NET 8) | Handler (`IInboxHandler<T>`) |
| `Relay.Sample.ManualInbox` | Azure Functions v4 isolated worker (.NET 8) | Client (`IInboxClient`) |

`Relay.Sample` and `Relay.Sample.AzureFunctions` use the **handler mode** — you register `IInboxHandler<T>` implementations and `IInboxProcessor` calls them automatically. `Relay.Sample.ManualInbox` uses the **client mode** — no handler classes, you fetch pending messages and process them yourself with `IInboxClient`.

---

## The Pattern

The transactional inbox/outbox pattern decouples three concerns that are normally tangled together:

```
                    ┌─────────────────────────────────────┐
  External caller   │           Your service              │
                    │                                     │
  POST /orders ───► │ IInboxReceiver   InboxMessages (DB) │
                    │    ▼ store only, no business logic   │
                    │                                     │
                    │ IInboxProcessor  ◄── periodic sweep │
                    │    ▼ handler runs                   │
                    │ IOutboxWriter    OutboxMessages (DB) │
                    │    ▼ stage, don't send              │
                    │                                     │
                    │ IOutboxDispatcher ◄── periodic sweep│
                    │    ▼ publisher runs                 │
  Downstream ◄───── │ IOutboxPublisher                    │
                    └─────────────────────────────────────┘
```

**Why?**

- **Idempotency** — the inbox deduplicates by idempotency key before any work runs.
- **Durability** — messages survive crashes because they are persisted before processing.
- **Decoupled delivery** — outbox messages are stored first; delivery to downstream services is a separate, retriable step.
- **Correlation** — every outbox message carries the ID of the inbox message that caused it, giving you a full cause→effect trace.

---

## Domain

Both projects use the same scenario: an order management service that receives orders from an external checkout service and fans out to a warehouse and a notifications service.

### Events

```
Inbound (inbox boundary)
  OrderPlaced(OrderId, CustomerId, Items[], Total)

Outbound (outbox → downstream)
  OrderFulfillmentRequested(OrderId, CustomerId, Items[])  → warehouse
  OrderConfirmationSent(OrderId, CustomerId, Total)        → notifications
```

### Handler

`OrderPlacedHandler` implements `IInboxHandler<OrderPlaced>`. It runs inside the inbox processor after a message has been pulled from the DB.

```csharp
public string GetIdempotencyKey(OrderPlaced msg) => $"order-placed:{msg.OrderId}";

public async Task HandleAsync(OrderPlaced msg, CancellationToken ct = default)
{
    await outbox.WriteAsync(new OrderFulfillmentRequested(...), "orders", "warehouse.fulfil", ct);
    await outbox.WriteAsync(new OrderConfirmationSent(...),     "orders", "notifications.email", ct);
}
```

Two outbox messages are staged per order. No message is sent anywhere during `HandleAsync` — only stored.

### Publishers

`FulfillmentPublisher` and `ConfirmationPublisher` implement `IOutboxPublisher<T>`. In this sample they log via `ILogger`. In production swap the body for a Service Bus send, RabbitMQ publish, HTTP call, etc.

---

## Relay.Sample — ASP.NET Core

```
Relay.Sample/
├── Domain/
│   ├── Events.cs
│   ├── OrderPlacedHandler.cs
│   └── Publishers.cs
├── Infrastructure/
│   ├── SqliteInboxStore.cs          IInboxStore → SQLite
│   ├── SqliteOutboxStore.cs         IOutboxStore → SQLite
│   └── InfrastructureExtensions.cs  AddSqliteRelayStores()
├── Workers/
│   ├── InboxWorker.cs               BackgroundService, PeriodicTimer
│   └── OutboxWorker.cs              BackgroundService, PeriodicTimer
├── appsettings.json
└── Program.cs                       WebApplication, endpoints
```

### How it starts

`Program.cs` calls `WebApplication.CreateBuilder`, registers services, then `app.Run()`:

1. `AddInbox("orders", ...)` — registers `IInboxReceiver<OrderPlaced>`, `IInboxProcessor`, handler, and inbox options.
2. `AddOutbox("orders", ...)` — registers `IOutboxWriter`, `IOutboxDispatcher`, publishers, and outbox options.
3. `AddSqliteRelayStores(connStr)` — registers `IInboxStore` and `IOutboxStore` (SQLite), and schedules `SqliteSchemaInitializer` as a hosted service that runs `CREATE TABLE IF NOT EXISTS` before Kestrel accepts connections.
4. `AddHostedService<InboxWorker>()` and `AddHostedService<OutboxWorker>()` — start polling in the background.

### Endpoints

| Method | Path | Description |
|---|---|---|
| `POST` | `/orders` | Receive an order — calls `IInboxReceiver`, returns `202 Accepted` or `200` (duplicate) |
| `GET` | `/stats` | Inbox + outbox stats as JSON |
| `GET` | `/orders/dead` | Dead-lettered inbox messages |
| `POST` | `/orders/{id}/requeue` | Requeue a dead-lettered message |
| `POST` | `/demo/seed` | Seeds 3 test orders — useful for local testing |

### Workers

Both workers follow the same pattern:

```csharp
using var timer = new PeriodicTimer(_interval);   // from appsettings.json

while (await timer.WaitForNextTickAsync(ct))
{
    await using var scope = sp.CreateAsyncScope(); // fresh scope per tick
    var processor = scope.ServiceProvider.GetRequiredService<IInboxProcessor>();
    var result    = await processor.ProcessPendingAsync("orders", ct: ct);
    // log result
}
```

A new DI scope is created per tick so scoped services (handlers, publishers) are properly disposed after each batch.

### Configuration (`appsettings.json`)

```json
{
  "Relay": {
    "Database": "relay-sample.db",
    "Inbox":  { "PollingIntervalSeconds": 5 },
    "Outbox": { "PollingIntervalSeconds": 5 }
  }
}
```

### Running

```bash
cd Relay.Sample
dotnet run
# App starts on http://localhost:5000

curl -X POST http://localhost:5000/demo/seed   # receive 3 orders
curl        http://localhost:5000/stats         # watch pending → processed → published
```

---

## Relay.Sample.AzureFunctions — Azure Functions v4

```
Relay.Sample.AzureFunctions/
├── Domain/                          (identical to Relay.Sample)
│   ├── Events.cs
│   ├── OrderPlacedHandler.cs
│   └── Publishers.cs
├── Infrastructure/                  (identical logic, AzureFunctions namespace)
│   ├── SqliteInboxStore.cs
│   ├── SqliteOutboxStore.cs
│   └── InfrastructureExtensions.cs
├── Functions/
│   ├── OrdersFunction.cs            HTTP Trigger  — POST /api/orders
│   ├── ProcessInboxFunction.cs      Timer Trigger — replaces InboxWorker
│   └── DispatchOutboxFunction.cs    Timer Trigger — replaces OutboxWorker
├── Program.cs                       HostBuilder + ConfigureFunctionsWorkerDefaults
├── host.json
└── local.settings.json
```

### How it maps to the Web API version

| Relay.Sample | Relay.Sample.AzureFunctions |
|---|---|
| `WebApplication.CreateBuilder` | `HostBuilder` + `ConfigureFunctionsWorkerDefaults` |
| `app.MapPost("/orders", ...)` | `[HttpTrigger]` on `OrdersFunction` |
| `InboxWorker` : `BackgroundService` | `[TimerTrigger]` on `ProcessInboxFunction` |
| `OutboxWorker` : `BackgroundService` | `[TimerTrigger]` on `DispatchOutboxFunction` |
| `CreateAsyncScope()` in worker | Azure Functions creates a scope per invocation automatically |

### How it starts (`Program.cs`)

```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        services.AddInbox("orders", inbox => inbox
            .WithHandler<OrderPlaced, OrderPlacedHandler>()
            .WithOptions(o => { o.CorrelationScope = OutboxCorrelationContext.Set; }));

        services.AddOutbox("orders", outbox => outbox
            .WithPublisher<OrderFulfillmentRequested, FulfillmentPublisher>()
            .WithPublisher<OrderConfirmationSent, ConfirmationPublisher>());

        services.AddSqliteRelayStores(connStr);
    })
    .Build();

host.Run();
```

Relay registration is the same as the Web API. The only difference is the outer host.

### Function classes

**`OrdersFunction`** — HTTP Trigger

Azure Functions creates a new DI scope per invocation, so `IInboxReceiver<OrderPlaced>` can be constructor-injected directly.

```csharp
[Function("ReceiveOrder")]
public async Task<HttpResponseData> ReceiveOrder(
    [HttpTrigger(AuthorizationLevel.Function, "post", Route = "orders")] HttpRequestData req,
    CancellationToken ct)
{
    var order  = await req.ReadFromJsonAsync<OrderPlaced>(ct);
    var result = await receiver.ReceiveAsync(order!, source: "azure-functions-http", ct: ct);
    // return 202 Accepted or 200 duplicate
}
```

**`ProcessInboxFunction`** — Timer Trigger

Replaces `InboxWorker`. Runs on the NCRONTAB expression in the `Relay__InboxCron` app setting. `IInboxProcessor` is scoped and injected directly — Azure Functions scopes it per invocation.

```csharp
[Function("ProcessInbox")]
public async Task Run(
    [TimerTrigger("%Relay__InboxCron%", RunOnStartup = true)] TimerInfo timer,
    CancellationToken ct)
{
    var result = await processor.ProcessPendingAsync("orders", ct: ct);
    // log result
}
```

`RunOnStartup = true` means both timer functions fire once immediately on cold start, which empties any messages left over from a previous run before the regular schedule begins.

**`DispatchOutboxFunction`** — Timer Trigger

Same shape as `ProcessInboxFunction`, but calls `IOutboxDispatcher.DispatchPendingAsync`.

### Configuration (`local.settings.json`)

```json
{
  "Values": {
    "AzureWebJobsStorage":      "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "Relay__Database":          "relay-functions.db"
  }
}
```

Azure Functions maps `Relay__Database` (double underscore) to `Relay:Database` in `IConfiguration`, matching the same key the code reads.

The timer schedules are hardcoded as `private const string Schedule = "*/30 * * * * *"` in each function class. The `%AppSetting%` substitution syntax in `TimerTrigger` only resolves at host indexing time (before `Program.cs` runs), so it requires the key to exist in the host's app settings at startup — not in `IConfiguration`. To make the schedule configurable without redeploying, replace the constant with `"%InboxCron%"` and add `"InboxCron": "*/30 * * * * *"` to Application Settings in the Azure portal.

### Running locally

Requires [Azure Functions Core Tools](https://learn.microsoft.com/azure/azure-functions/functions-run-local) and [Azurite](https://learn.microsoft.com/azure/storage/common/storage-use-azurite) for local storage emulation.

```bash
azurite &                                                # start local storage emulator
cd Relay.Sample.AzureFunctions
func start                                               # start functions host

curl -X POST http://localhost:7071/api/orders/seed       # seed test orders
curl -X POST http://localhost:7071/api/orders \
     -H "Content-Type: application/json" \
     -d '{"orderId":"ORD-004","customerId":"CUST-D","items":["Sprocket"],"total":9.99}'
```

---

## Relay.Sample.ManualInbox — Azure Functions (Client Mode)

```
Relay.Sample.ManualInbox/
├── Domain/
│   └── Events.cs                    PaymentReceived, RefundIssued (no handlers)
├── Infrastructure/
│   ├── SqliteInboxStore.cs          IInboxStore → SQLite
│   └── InfrastructureExtensions.cs  UseInboxClientSqliteStore()
├── Functions/
│   ├── PaymentsFunction.cs          HTTP Triggers — receive payments, refunds, stats
│   └── ProcessInboxFunction.cs      Timer Trigger — manual inbox processing
├── Program.cs                       HostBuilder + AddInboxClient() (no handlers)
├── host.json
└── local.settings.json

Relay.Sample.ManualInbox.Tests/
└── ManualInboxTests.cs              25 unit tests covering the client-mode flow
```

### How it differs from the handler-mode samples

In `Relay.Sample.AzureFunctions`, you register `IInboxHandler<OrderPlaced>` and call `IInboxProcessor.ProcessPendingAsync()` — the library resolves the handler, deserialises the payload, and calls `HandleAsync` for you.

In `Relay.Sample.ManualInbox`, there are **no handler classes** and **no `IInboxProcessor`**. Instead:

1. `AddInboxClient()` registers `IInboxClient` — a raw-mode API for both receiving and processing.
2. HTTP triggers call `IInboxClient.ReceiveAsync(inboxName, message, idempotencyKey)` to store messages.
3. A timer trigger (simulating a Service Bus trigger) calls `IInboxClient.GetPendingAsync()`, switches on `msg.Type` to deserialise, runs inline processing logic, then calls `MarkProcessedAsync` or `MarkFailedAsync`.

This is useful when:
- You don't want to create a handler class per message type.
- Your processing logic is simple enough to live in the function itself.
- You want full control over the process → mark-{processed,failed} lifecycle.
- You're retrofitting an existing codebase that already has processing logic elsewhere.

### The flow

```
                    ┌─────────────────────────────────────────────────┐
  External caller   │           Your Azure Functions app             │
                    │                                                │
  POST /api/       ─┤                                                │
  payments          │  PaymentsFunction                              │
  POST /api/       ─┤     IInboxClient.ReceiveAsync(...)             │
  refunds           │         ▼                                      │
                    │     InboxMessages (SQLite)                     │
                    │         ▼                                      │
                    │  ProcessInboxFunction (Timer Trigger / 30s)    │
                    │     IInboxClient.GetPendingAsync(...)           │
                    │     switch (msg.Type)                          │
                    │         case "PaymentReceived": ...             │
                    │         case "RefundIssued": ...                │
                    │     IInboxClient.MarkProcessedAsync(msg)       │
                    └─────────────────────────────────────────────────┘
```

> **Note:** The timer trigger is a stand-in. In production you would replace it with a
> Service Bus trigger — when a message is written to the inbox, `OnMessageStored` publishes
> an event to Service Bus, and the `ProcessInboxFunction` is triggered by that event instead
> of polling.

### How it starts (`Program.cs`)

```csharp
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        var connStr = $"Data Source={ctx.Configuration["Relay:Database"] ?? "relay-manual-inbox.db"}";

        services
            .AddInboxClient(o =>
            {
                o.OnDeadLettered = (msg, ex) => { /* alert */ };
                o.OnMessageStored = msg => { /* publish to Service Bus */ };
            })
            .UseInboxClientSqliteStore(connStr);
    })
    .Build();
```

### Function classes

**`PaymentsFunction`** — HTTP Triggers

```csharp
[Function("ReceivePayment")]
public async Task<HttpResponseData> ReceivePayment(
    [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "payments")] HttpRequestData req,
    CancellationToken ct)
{
    var payment = await req.ReadFromJsonAsync<PaymentReceived>(ct);
    var result  = await inbox.ReceiveAsync("payments", payment!, $"payment:{payment!.PaymentId}",
                                           source: "azure-functions-http", ct: ct);
    // return Accepted or OK (duplicate)
}
```

Also exposes `POST /api/refunds`, `GET /api/stats`, `GET /api/payments/dead`, `POST /api/payments/{id}/requeue`, and `POST /api/demo/seed`.

**`ProcessInboxFunction`** — Timer Trigger (simulates Service Bus)

```csharp
[Function("ProcessInbox")]
public async Task Run(
    [TimerTrigger("*/30 * * * * *", RunOnStartup = true, UseMonitor = false)] TimerInfo timer,
    CancellationToken ct)
{
    var messages = await inbox.GetPendingAsync("payments", batchSize: 50, ct);
    foreach (var msg in messages)
    {
        switch (msg.Type)
        {
            case nameof(PaymentReceived):
                var payment = JsonSerializer.Deserialize<PaymentReceived>(msg.Payload)!;
                // process payment...
                break;
            case nameof(RefundIssued):
                var refund = JsonSerializer.Deserialize<RefundIssued>(msg.Payload)!;
                // process refund...
                break;
        }
        await inbox.MarkProcessedAsync(msg, ct);
    }
}
```

### Configuration (`local.settings.json`)

```json
{
  "Values": {
    "AzureWebJobsStorage":      "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated",
    "Relay__Database":          "relay-manual-inbox.db"
  }
}
```

### Running locally

```bash
azurite &
cd Relay.Sample.ManualInbox
func start

curl -X POST http://localhost:7071/api/demo/seed
curl         http://localhost:7071/api/stats
curl -X POST http://localhost:7071/api/payments \
     -H "Content-Type: application/json" \
     -d '{"paymentId":"PAY-004","customerId":"CUST-D","amount":9.99,"currency":"USD"}'
```

### Tests

The `Relay.Sample.ManualInbox.Tests` project contains 25 unit tests that exercise the `IInboxClient` flow end-to-end using `InMemoryInboxStore` — no Azure Functions SDK or HTTP infrastructure needed:

```bash
dotnet test Relay.Sample.ManualInbox.Tests
```

Tests cover: receive, deduplication, JSON payload round-trip, `GetPending`, `MarkProcessed`, `MarkFailed`, dead-lettering after max retries, hooks (`OnMessageStored`, `OnProcessed`, `OnDuplicate`, `OnDeadLettered`), batch size, source timestamps, stats, requeue, inbox isolation, and a full manual-processing flow that mirrors exactly what `ProcessInboxFunction` does.

---

## SQLite store

Both samples share the same SQLite store implementation (`Infrastructure/SqliteInboxStore.cs` and `SqliteOutboxStore.cs`). Key differences from the built-in `SqlInboxStore` / `SqlOutboxStore` (which target SQL Server):

| SQL Server store | SQLite store |
|---|---|
| `UNIQUEIDENTIFIER` | `TEXT` (GUIDs stored as strings) |
| `NVARCHAR(MAX)` | `TEXT` |
| `SYSUTCDATETIME()` | `datetime('now')` |
| `TOP (@n)` | `LIMIT @n` |
| `WITH (UPDLOCK, READPAST)` | Not needed (no hint syntax in SQLite) |
| `IF NOT EXISTS (SELECT 1 FROM sys.tables ...)` | `CREATE TABLE IF NOT EXISTS` |
| Multi-result batch for stats | Separate commands per query |

Schema is created automatically on startup by `SqliteSchemaInitializer` (a hosted service registered by `AddSqliteRelayStores`).

---

## Correlation

When `o.CorrelationScope = OutboxCorrelationContext.Set` is set, the inbox processor calls `OutboxCorrelationContext.Set(inboxMessageId)` before invoking each handler. `IOutboxWriter` reads that ambient async-local value and stamps every outbox message it writes with `CorrelationId = inboxMessageId`.

This means you can query:

```csharp
var outboxMessages = await outboxStore.GetByCorrelationIdAsync(inboxMessageId);
```

to see every downstream message caused by a single incoming order — a full cause→effect trace across the inbox/outbox boundary.
