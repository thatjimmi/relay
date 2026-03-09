using Microsoft.Extensions.Hosting;
using Relay.Inbox.Extensions;
using Relay.Sample.ManualInbox.Infrastructure;

// Azure Functions isolated worker model uses HostBuilder directly.
// This sample demonstrates the "manual / client mode" inbox pattern:
//   • No IInboxHandler<T> — processing logic lives in the timer function itself.
//   • IInboxClient handles both receiving and manual processing.
//   • HTTP Trigger functions replace Minimal API endpoints.
//   • Timer Trigger function replaces the BackgroundService InboxWorker.
//     (In production this would be a Service Bus trigger; we use a timer here
//      because we don't have a Service Bus instance.)

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((ctx, services) =>
    {
        // Connection string from local.settings.json (local) or Application Settings (Azure).
        var db = ctx.Configuration["Relay:Database"] ?? "relay-manual-inbox.db";
        var connStr = $"Data Source={db}";

        // ── Inbox (raw / client mode) ─────────────────────────────────────
        // No .WithHandler<T, THandler>() and no AddInbox() — just AddInboxClient().
        // IInboxClient handles both receiving (with explicit idempotency key) and
        // processing (fetch → your own logic → MarkProcessed/MarkFailed).
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
            })
            .UseInboxClientSqliteStore(connStr);
    })
    .Build();

host.Run();
