using Microsoft.Extensions.Hosting;
using Relay.Inbox.Extensions;
using Relay.Outbox.Extensions;
using Relay.Outbox.Internal;
using Relay.Sample.AzureFunctions.Domain;
using Relay.Sample.AzureFunctions.Infrastructure;

// Azure Functions isolated worker model uses HostBuilder directly.
// The pattern is identical to the Relay.Sample Web API, except:
//   • No WebApplication / Kestrel
//   • No BackgroundService workers (Azure Functions runtime handles scheduling)
//   • Timer Trigger functions replace InboxWorker / OutboxWorker

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        // Connection string from local.settings.json (local) or Application Settings (Azure).
        // Azure Functions maps "Relay__Database" env var → "Relay:Database" in IConfiguration.
        var db = ctx.Configuration["Relay:Database"] ?? "relay-functions.db";
        var connStr = $"Data Source={db}";

        // ── Inbox ─────────────────────────────────────────────────────────
        services.AddInbox("orders", inbox => inbox
            .WithHandler<OrderPlaced, OrderPlacedHandler>()
            .WithOptions(o =>
            {
                o.CorrelationScope = OutboxCorrelationContext.Set;
                o.OnDeadLettered = (msg, ex) =>
                {
                    // In production: alert PagerDuty / Slack / Application Insights
                    Console.Error.WriteLine(
                        $"[DEAD LETTER] inbox={msg.InboxName} key={msg.IdempotencyKey} error={ex.Message}");
                    return Task.CompletedTask;
                };
            }));

        // ── Outbox ────────────────────────────────────────────────────────
        services.AddOutbox("orders", outbox => outbox
            .WithPublisher<OrderFulfillmentRequested, FulfillmentPublisher>()
            .WithPublisher<OrderConfirmationSent, ConfirmationPublisher>()
            .OnDeadLettered((msg, ex) =>
            {
                Console.Error.WriteLine(
                    $"[DEAD LETTER] outbox={msg.OutboxName} type={msg.Type} error={ex.Message}");
                return Task.CompletedTask;
            }));

        // ── Infrastructure ────────────────────────────────────────────────
        // Registers IInboxStore + IOutboxStore (SQLite) and runs EnsureSchemaAsync
        // via a hosted service before the first function invocation is served.
        services.AddSqliteRelayStores(connStr);
    })
    .Build();

host.Run();
