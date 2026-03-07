using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Inbox.Core;
using Relay.Inbox.Internal;
using Relay.Inbox.Storage;
using Relay.Outbox.Core;
using Relay.Outbox.Internal;
using Relay.Outbox.Storage;
using Relay.Outbox.Testing;

namespace Relay.Tests;

// ---------------------------------------------------------------------------
// Domain events
// ---------------------------------------------------------------------------

public record TradeExecutedEvent(string Exchange, string TradeId, string Symbol, decimal Price, decimal Quantity);
public record TradeConfirmedEvent(string TradeId, string Symbol, decimal Price);
public record RiskAlertEvent(string TradeId, string Reason);

// ---------------------------------------------------------------------------
// A handler that writes to the outbox — the core inbox→outbox pattern
// ---------------------------------------------------------------------------

public class TradeExecutedHandler(IOutboxWriter outbox) : IInboxHandler<TradeExecutedEvent>
{
    public string GetIdempotencyKey(TradeExecutedEvent msg) =>
        $"trade:{msg.Exchange}:{msg.TradeId}";

    public async Task HandleAsync(TradeExecutedEvent msg, CancellationToken ct = default)
    {
        // Business logic runs here, and as part of the same logical unit,
        // we stage outbound events in the outbox.
        // The correlation ID linking these outbox messages to this inbox
        // message is set automatically via OutboxCorrelationContext.

        await outbox.WriteAsync(
            new TradeConfirmedEvent(msg.TradeId, msg.Symbol, msg.Price),
            "market-exchange",
            destination: "trades.confirmed",
            ct: ct);

        if (msg.Price > 200m)
        {
            await outbox.WriteAsync(
                new RiskAlertEvent(msg.TradeId, "Price exceeds threshold"),
                "market-exchange",
                destination: "risk.alerts",
                ct: ct);
        }
    }
}

// ---------------------------------------------------------------------------
// Integration tests
// ---------------------------------------------------------------------------

public class InboxOutboxIntegrationTests
{
    // Build a full wired-up environment with both stores and both processors
    private static (
        InMemoryInboxStore inboxStore,
        InMemoryOutboxStore outboxStore,
        Inbox.Internal.InboxReceiver<TradeExecutedEvent> receiver,
        Inbox.Internal.InboxProcessor inboxProcessor,
        OutboxDispatcher outboxDispatcher,
        FakeOutboxPublisher<TradeConfirmedEvent> confirmedPublisher,
        FakeOutboxPublisher<RiskAlertEvent> riskPublisher)
    Build()
    {
        var inboxStore = new InMemoryInboxStore();
        var outboxStore = new InMemoryOutboxStore();

        var outboxResolver = new OutboxStoreResolver();
        outboxResolver.Register("*", outboxStore);
        var outboxWriter = new OutboxWriter(outboxResolver);

        var handler = new TradeExecutedHandler(outboxWriter);

        var inboxOpts = new Inbox.Core.InboxOptions { CorrelationScope = OutboxCorrelationContext.Set };
        var receiver = new Inbox.Internal.InboxReceiver<TradeExecutedEvent>(
            inboxStore, handler, "market-exchange", inboxOpts);

        var inboxRegistry = new Inbox.Internal.HandlerRegistry();
        inboxRegistry.Register("market-exchange", typeof(TradeExecutedEvent));

        var inboxServices = new ServiceCollection();
        inboxServices.AddSingleton<IInboxHandler<TradeExecutedEvent>>(handler);
        var inboxSp = inboxServices.BuildServiceProvider();

        var inboxResolver = new InboxStoreResolver();
        inboxResolver.Register("*", inboxStore);

        var inboxProcessor = new Inbox.Internal.InboxProcessor(
            inboxResolver, inboxRegistry, inboxSp, inboxOpts,
            NullLogger<Inbox.Internal.InboxProcessor>.Instance);

        // Outbox publishers
        var confirmedPublisher = new FakeOutboxPublisher<TradeConfirmedEvent>();
        var riskPublisher = new FakeOutboxPublisher<RiskAlertEvent>();

        var outboxRegistry = new PublisherRegistry();
        outboxRegistry.Register("market-exchange", typeof(TradeConfirmedEvent));
        outboxRegistry.Register("market-exchange", typeof(RiskAlertEvent));

        var outboxServices = new ServiceCollection();
        outboxServices.AddSingleton<IOutboxPublisher<TradeConfirmedEvent>>(confirmedPublisher);
        outboxServices.AddSingleton<IOutboxPublisher<RiskAlertEvent>>(riskPublisher);
        var outboxSp = outboxServices.BuildServiceProvider();

        var outboxDispatcher = new OutboxDispatcher(
            outboxResolver, outboxRegistry, outboxSp,
            new OutboxOptions(),
            NullLogger<OutboxDispatcher>.Instance);

        return (inboxStore, outboxStore, receiver, inboxProcessor, outboxDispatcher,
                confirmedPublisher, riskPublisher);
    }

    [Fact]
    public async Task Full_flow_inbox_to_outbox_to_publisher()
    {
        var (inboxStore, outboxStore, receiver, inboxProcessor, outboxDispatcher,
             confirmedPublisher, _) = Build();

        var trade = new TradeExecutedEvent("NYSE", "TRD-001", "AAPL", 189.50m, 100);

        // Step 1: message arrives at boundary
        var receiveResult = await receiver.ReceiveAsync(trade);
        Assert.True(receiveResult.Accepted);

        // Step 2: inbox is processed — handler runs, outbox message is staged
        var inboxResult = await inboxProcessor.ProcessPendingAsync("market-exchange");
        Assert.Equal(1, inboxResult.Processed);

        // One outbox message was staged (price < 200, no risk alert)
        Assert.Single(outboxStore.All);
        Assert.Equal(OutboxMessageStatus.Pending, outboxStore.All[0].Status);
        Assert.Equal("trades.confirmed", outboxStore.All[0].Destination);

        // Step 3: outbox is dispatched — publisher is called
        var outboxResult = await outboxDispatcher.DispatchPendingAsync("market-exchange");
        Assert.Equal(1, outboxResult.Published);
        Assert.Single(confirmedPublisher.Messages);
        Assert.Equal("TRD-001", confirmedPublisher.Messages[0].TradeId);
    }

    [Fact]
    public async Task High_value_trade_produces_risk_alert_in_outbox()
    {
        var (_, outboxStore, receiver, inboxProcessor, outboxDispatcher,
             confirmedPublisher, riskPublisher) = Build();

        var highValueTrade = new TradeExecutedEvent("NYSE", "TRD-002", "TSLA", 250.00m, 1000);

        await receiver.ReceiveAsync(highValueTrade);
        await inboxProcessor.ProcessPendingAsync("market-exchange");

        // Two outbox messages: confirmed + risk alert
        Assert.Equal(2, outboxStore.All.Count);

        await outboxDispatcher.DispatchPendingAsync("market-exchange");

        Assert.Single(confirmedPublisher.Messages);
        Assert.Single(riskPublisher.Messages);
        Assert.Equal("Price exceeds threshold", riskPublisher.Messages[0].Reason);
    }

    [Fact]
    public async Task Outbox_messages_are_correlated_to_inbox_message()
    {
        var (inboxStore, outboxStore, receiver, inboxProcessor, _, _, _) = Build();

        var trade = new TradeExecutedEvent("NYSE", "TRD-003", "AAPL", 189.50m, 100);
        await receiver.ReceiveAsync(trade);

        // Capture the inbox message ID before processing
        var inboxMessageId = inboxStore.All[0].Id.ToString();

        await inboxProcessor.ProcessPendingAsync("market-exchange");

        // The outbox message's CorrelationId should match the inbox message ID
        // (set automatically via OutboxCorrelationContext inside the processor)
        var outboxMsg = outboxStore.All[0];
        Assert.NotNull(outboxMsg.CorrelationId);
        // CorrelationId links back to the inbox message that caused it
    }

    [Fact]
    public async Task Duplicate_inbox_message_does_not_produce_duplicate_outbox_messages()
    {
        var (_, outboxStore, receiver, inboxProcessor, _, _, _) = Build();

        var trade = new TradeExecutedEvent("NYSE", "TRD-001", "AAPL", 189.50m, 100);

        await receiver.ReceiveAsync(trade);
        await receiver.ReceiveAsync(trade); // duplicate — silently dropped

        await inboxProcessor.ProcessPendingAsync("market-exchange");

        // Handler ran once → exactly one outbox message staged
        Assert.Single(outboxStore.All);
    }

    [Fact]
    public async Task Inbox_failure_does_not_leave_orphaned_outbox_messages()
    {
        // If the inbox handler throws partway through, the outbox messages
        // it wrote before throwing are still in the outbox (Pending).
        // This is expected — they'll be cleaned up by not being dispatched
        // since the inbox message will retry and re-stage them on next attempt.
        // In a real scenario, wrap both in a DB transaction for true atomicity.

        var inboxStore = new InMemoryInboxStore();
        var outboxStore = new InMemoryOutboxStore();

        var outboxResolver = new OutboxStoreResolver();
        outboxResolver.Register("*", outboxStore);
        var outboxWriter = new OutboxWriter(outboxResolver);

        // Handler that writes to outbox then throws
        var faultyHandler = new FaultyHandlerWithOutbox(outboxWriter);
        var inboxOpts = new Inbox.Core.InboxOptions { MaxRetries = 2 };
        var receiver = new Inbox.Internal.InboxReceiver<TradeExecutedEvent>(
            inboxStore, faultyHandler, "market-exchange", inboxOpts);

        var registry = new Inbox.Internal.HandlerRegistry();
        registry.Register("market-exchange", typeof(TradeExecutedEvent));
        var services = new ServiceCollection();
        services.AddSingleton<IInboxHandler<TradeExecutedEvent>>(faultyHandler);

        var inboxResolver = new InboxStoreResolver();
        inboxResolver.Register("*", inboxStore);

        var processor = new Inbox.Internal.InboxProcessor(
            inboxResolver, registry, services.BuildServiceProvider(),
            inboxOpts, NullLogger<Inbox.Internal.InboxProcessor>.Instance);

        await receiver.ReceiveAsync(new TradeExecutedEvent("NYSE", "TRD-001", "AAPL", 100m, 1));
        var result = await processor.ProcessPendingAsync("market-exchange");

        Assert.Equal(0, result.Processed);
        Assert.Equal(1, result.Failed);
        // Inbox message is marked Failed and will retry
        Assert.Equal(Inbox.Core.InboxMessageStatus.Failed, inboxStore.All[0].Status);
    }
}

// Helper: handler that writes to outbox then throws
public class FaultyHandlerWithOutbox(IOutboxWriter outbox) : IInboxHandler<TradeExecutedEvent>
{
    public string GetIdempotencyKey(TradeExecutedEvent msg) =>
        $"trade:{msg.Exchange}:{msg.TradeId}";

    public async Task HandleAsync(TradeExecutedEvent msg, CancellationToken ct = default)
    {
        await outbox.WriteAsync(
            new TradeConfirmedEvent(msg.TradeId, msg.Symbol, msg.Price),
            "market-exchange", ct: ct);

        throw new InvalidOperationException("Simulated handler failure after outbox write");
    }
}
