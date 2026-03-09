using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Relay.Inbox.Extensions;
using Relay.Outbox.Core;
using Relay.Outbox.Extensions;
using Relay.Sample.Trayport.Domain;
using Relay.Sample.Trayport.Infrastructure;

// ─────────────────────────────────────────────────────────────────────────────
// Relay.Sample.Trayport — Azure Functions (Isolated Worker, .NET 8)
//
// Demonstrates the "raw / client mode" inbox with outbox fan-out:
//   • IInboxClient handles receiving Trayport trades (no IInboxHandler<T>).
//   • Timer Trigger manually processes trades: map → fan-out via IOutboxWriter.
//   • Separate Timer Trigger dispatches outbox messages via publishers.
//   • Source-timestamp support for trade amendments.
// ─────────────────────────────────────────────────────────────────────────────

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        var db = ctx.Configuration["Relay:Database"] ?? "trayport-inbox.db";
        var connStr = $"Data Source={db}";

        // ── Inbox (raw / client mode) ─────────────────────────────────────
        // No .WithHandler<T, THandler>() — processing logic lives in the
        // ProcessTradesFunction timer. IInboxClient handles receive + manual
        // get-pending / mark-processed / mark-failed.
        services
            .AddInboxClient(o =>
            {
                o.OnDeadLettered = (msg, ex) =>
                {
                    Console.Error.WriteLine(
                        $"[DEAD LETTER] inbox={msg.InboxName} key={msg.IdempotencyKey} error={ex.Message}");
                    return Task.CompletedTask;
                };
                o.OnMessageStored = msg =>
                {
                    Console.WriteLine(
                        $"[STORED] inbox={msg.InboxName} key={msg.IdempotencyKey} id={msg.Id}");
                    return Task.CompletedTask;
                };
                o.OnProcessed = msg =>
                {
                    Console.WriteLine(
                        $"[PROCESSED] inbox={msg.InboxName} key={msg.IdempotencyKey} id={msg.Id}");
                    return Task.CompletedTask;
                };
            })
            .UseInboxClientSqliteStore(connStr, "TradeInboxMessages");

        // ── Outbox (fan-out to risk + settlement) ─────────────────────────
        // Two publishers: one for risk events, one for settlement requests.
        // Correlation is set manually in the timer function via
        // OutboxCorrelationContext so outbox messages link back to the
        // inbox message that caused them.
        //
        // The store is created once and registered as both:
        //   - IOutboxStore (so functions like TradeInboxFunction can inject it for stats)
        //   - per-outbox store via the builder (so OutboxWriter / OutboxDispatcher use it)
        var outboxStore = new SqliteOutboxStore(connStr, "TradeOutboxMessages");
        services.AddSingleton<IOutboxStore>(outboxStore);

        services.AddOutbox("trades", outbox => outbox
            .WithPublisher<RiskTradeEvent, RiskPublisher>()
            .WithPublisher<SettlementRequest, SettlementPublisher>()
            .UseStore(outboxStore)
            .OnMessageStored(msg =>
            {
                Console.WriteLine(
                    $"[OUTBOX STORED] outbox={msg.OutboxName} id={msg.Id} type={msg.Type} corr={msg.CorrelationId}");
                return Task.CompletedTask;
            })
            .OnDeadLettered((msg, ex) =>
            {
                Console.Error.WriteLine(
                    $"[OUTBOX DEAD] outbox={msg.OutboxName} type={msg.Type} error={ex.Message}");
                return Task.CompletedTask;
            }));
    })
    .Build();

host.Run();
