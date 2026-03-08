using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Inbox.Core;
using Relay.Inbox.Extensions;
using Relay.Inbox.Internal;
using Relay.Inbox.Storage;
using Relay.Inbox.Testing;

namespace Relay.Inbox.Tests;

// ---------------------------------------------------------------------------
// Sample domain events (imagine these come from a market exchange WebSocket)
// ---------------------------------------------------------------------------

public record TradeExecutedEvent(
    string Exchange,
    string ExternalTradeId,
    string Symbol,
    decimal Price,
    decimal Quantity,
    DateTime ExecutedAt);

public record OrderFilledEvent(
    string Exchange,
    string OrderId,
    string Symbol,
    decimal FilledQuantity);

// ---------------------------------------------------------------------------
// Sample handlers
// ---------------------------------------------------------------------------

public class TradeExecutedHandler : IInboxHandler<TradeExecutedEvent>
{
    public string GetIdempotencyKey(TradeExecutedEvent msg) =>
        $"trade:{msg.Exchange}:{msg.ExternalTradeId}";

    public Task HandleAsync(TradeExecutedEvent msg, CancellationToken ct = default) =>
        Task.CompletedTask;
}

public class OrderFilledHandler : IInboxHandler<OrderFilledEvent>
{
    // Uniqueness: exchange + orderId (an order can only be fully filled once)
    public string GetIdempotencyKey(OrderFilledEvent msg) =>
        $"order-fill:{msg.Exchange}:{msg.OrderId}";

    public Task HandleAsync(OrderFilledEvent msg, CancellationToken ct = default) =>
        Task.CompletedTask;
}

// ===========================================================================
// Tests
// ===========================================================================

public class InboxReceiverTests
{
    private static (InMemoryInboxStore store, IInboxReceiver<TradeExecutedEvent> receiver) Build(
        string inbox = "market")
    {
        var store = new InMemoryInboxStore();
        var handler = new TradeExecutedHandler();
        var opts = new InboxOptions();
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, inbox, opts);
        return (store, receiver);
    }

    private static TradeExecutedEvent NewTrade(string id = "TRD-001") =>
        new("NYSE", id, "AAPL", 189.50m, 100, DateTime.UtcNow);

    [Fact]
    public async Task Accepts_new_message_and_returns_stored()
    {
        var (store, receiver) = Build();

        var result = await receiver.ReceiveAsync(NewTrade());

        Assert.True(result.Accepted);
        Assert.False(result.WasDuplicate);
        Assert.NotNull(result.MessageId);
        Assert.Single(store.All);
    }

    [Fact]
    public async Task Deduplicates_same_message_silently()
    {
        var (store, receiver) = Build();
        var trade = NewTrade();

        await receiver.ReceiveAsync(trade);
        var second = await receiver.ReceiveAsync(trade); // exact same

        Assert.False(second.Accepted);
        Assert.True(second.WasDuplicate);
        Assert.Single(store.All); // still only one row
    }

    [Fact]
    public async Task Different_trade_ids_are_independent()
    {
        var (store, receiver) = Build();

        await receiver.ReceiveAsync(NewTrade("TRD-001"));
        await receiver.ReceiveAsync(NewTrade("TRD-002"));
        await receiver.ReceiveAsync(NewTrade("TRD-003"));

        Assert.Equal(3, store.All.Count);
    }

    [Fact]
    public async Task Stores_source_tag_when_provided()
    {
        var (store, receiver) = Build();

        await receiver.ReceiveAsync(NewTrade(), "binance-ws");

        Assert.Equal("binance-ws", store.All.Single().Source);
    }

    [Fact]
    public async Task Calls_OnDuplicate_hook_when_duplicate_received()
    {
        var duplicateKeys = new List<string>();
        var store = new InMemoryInboxStore();
        var handler = new TradeExecutedHandler();
        var opts = new InboxOptions { OnDuplicate = key => { duplicateKeys.Add(key); return Task.CompletedTask; } };
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, "market", opts);
        var trade = NewTrade();

        await receiver.ReceiveAsync(trade);
        await receiver.ReceiveAsync(trade);

        Assert.Single(duplicateKeys);
    }

    [Fact]
    public async Task Calls_OnMessageStored_after_successful_insert()
    {
        var stored = new List<InboxMessage>();
        var store = new InMemoryInboxStore();
        var handler = new TradeExecutedHandler();
        var opts = new InboxOptions { OnMessageStored = msg => { stored.Add(msg); return Task.CompletedTask; } };
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, "market", opts);

        await receiver.ReceiveAsync(NewTrade());

        Assert.Single(stored);
        Assert.Equal("market", stored[0].InboxName);
        Assert.Equal(nameof(TradeExecutedEvent), stored[0].Type);
    }

    [Fact]
    public async Task OnMessageStored_receives_assigned_message_id()
    {
        InboxMessage? captured = null;
        var store = new InMemoryInboxStore();
        var handler = new TradeExecutedHandler();
        var opts = new InboxOptions { OnMessageStored = msg => { captured = msg; return Task.CompletedTask; } };
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, "market", opts);

        var result = await receiver.ReceiveAsync(NewTrade());

        Assert.NotNull(captured);
        Assert.Equal(result.MessageId, captured!.Id);
    }

    [Fact]
    public async Task OnMessageStored_not_called_for_duplicate()
    {
        var stored = new List<InboxMessage>();
        var store = new InMemoryInboxStore();
        var handler = new TradeExecutedHandler();
        var opts = new InboxOptions { OnMessageStored = msg => { stored.Add(msg); return Task.CompletedTask; } };
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, "market", opts);
        var trade = NewTrade();

        await receiver.ReceiveAsync(trade);
        await receiver.ReceiveAsync(trade); // duplicate

        Assert.Single(stored); // only called once, not for the duplicate
    }

    // -------------------------------------------------------------------------
    // Source timestamp tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Source_timestamp_stored_on_new_message()
    {
        var (store, receiver) = Build();
        var ts = new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        await receiver.ReceiveAsync(NewTrade(), ts);

        Assert.Equal(ts, store.All.Single().SourceTimestamp);
    }

    [Fact]
    public async Task Newer_source_timestamp_updates_existing_message_and_returns_Updated()
    {
        var (store, receiver) = Build();
        var trade = NewTrade();
        var t1 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc);

        await receiver.ReceiveAsync(trade, t1);
        var updatedTrade = trade with { Price = 195.00m }; // payload changed
        var result = await receiver.ReceiveAsync(updatedTrade, t2);

        Assert.True(result.Accepted);
        Assert.True(result.WasUpdated);
        Assert.False(result.WasDuplicate);
        Assert.Single(store.All);                          // still one row
        Assert.Equal(t2, store.All.Single().SourceTimestamp);
        Assert.Contains("195", store.All.Single().Payload); // updated payload
    }

    [Fact]
    public async Task Same_source_timestamp_is_treated_as_duplicate()
    {
        var (store, receiver) = Build();
        var ts = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);

        await receiver.ReceiveAsync(NewTrade(), ts);
        var result = await receiver.ReceiveAsync(NewTrade() with { Price = 200m }, ts);

        Assert.False(result.Accepted);
        Assert.True(result.WasDuplicate);
    }

    [Fact]
    public async Task Older_source_timestamp_is_treated_as_duplicate()
    {
        var (store, receiver) = Build();
        var t1 = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc);
        var t0 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc); // earlier

        await receiver.ReceiveAsync(NewTrade(), t1);
        var result = await receiver.ReceiveAsync(NewTrade() with { Price = 200m }, t0);

        Assert.False(result.Accepted);
        Assert.True(result.WasDuplicate);
        Assert.Equal(t1, store.All.Single().SourceTimestamp); // unchanged
    }

    [Fact]
    public async Task First_message_with_timestamp_accepted_even_if_existing_has_no_timestamp()
    {
        var (store, receiver) = Build();
        var trade = NewTrade();

        // First receive: no timestamp
        await receiver.ReceiveAsync(trade);
        Assert.Null(store.All.Single().SourceTimestamp);

        // Second receive: with timestamp — stored timestamp is null so any timestamp is "newer"
        var ts = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var result = await receiver.ReceiveAsync(trade with { Price = 200m }, ts);

        Assert.True(result.Accepted);
        Assert.True(result.WasUpdated);
        Assert.Equal(ts, store.All.Single().SourceTimestamp);
    }

    [Fact]
    public async Task Updated_message_is_reset_to_Pending_so_processor_picks_it_up()
    {
        var store = new InMemoryInboxStore();
        var handler = new TradeExecutedHandler();
        var opts = new InboxOptions();
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, "market", opts);
        var trade = NewTrade();
        var t1 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc);

        await receiver.ReceiveAsync(trade, t1);

        // Simulate it having been processed already
        var msg = store.All.Single();
        msg.Status = InboxMessageStatus.Processed;

        // A newer update arrives — should reset to Pending
        await receiver.ReceiveAsync(trade with { Price = 200m }, t2);

        Assert.Equal(InboxMessageStatus.Pending, store.All.Single().Status);
    }

    [Fact]
    public async Task Updated_message_id_matches_original()
    {
        var (store, receiver) = Build();
        var trade = NewTrade();
        var t1 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc);

        var first = await receiver.ReceiveAsync(trade, t1);
        var second = await receiver.ReceiveAsync(trade with { Price = 200m }, t2);

        Assert.Equal(first.MessageId, second.MessageId); // same row
    }

    [Fact]
    public async Task OnMessageUpdated_hook_fires_on_update_not_on_new()
    {
        var updated = new List<InboxMessage>();
        var store = new InMemoryInboxStore();
        var handler = new TradeExecutedHandler();
        var opts = new InboxOptions { OnMessageUpdated = msg => { updated.Add(msg); return Task.CompletedTask; } };
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, "market", opts);
        var trade = NewTrade();
        var t1 = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
        var t2 = new DateTime(2024, 6, 1, 11, 0, 0, DateTimeKind.Utc);

        await receiver.ReceiveAsync(trade, t1);          // new — should NOT fire OnMessageUpdated
        await receiver.ReceiveAsync(trade, t2);          // update — should fire

        Assert.Single(updated);
        Assert.Equal(t2, updated[0].SourceTimestamp);
    }

    [Fact]
    public async Task No_timestamp_on_duplicate_still_returns_Duplicate()
    {
        var (store, receiver) = Build();
        var trade = NewTrade();

        await receiver.ReceiveAsync(trade, new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc));
        // Second call: no timestamp at all → original duplicate logic
        var result = await receiver.ReceiveAsync(trade);

        Assert.True(result.WasDuplicate);
    }
}

// ---------------------------------------------------------------------------

public class InboxProcessorTests
{
    private static (InMemoryInboxStore store, IInboxReceiver<TradeExecutedEvent> receiver,
                    InboxProcessor processor, FakeInboxHandler<TradeExecutedEvent> handler)
        Build(InboxOptions? options = null)
    {
        var store = new InMemoryInboxStore();
        var opts = options ?? new InboxOptions();
        var handler = new FakeInboxHandler<TradeExecutedEvent>(t => $"trade:{t.Exchange}:{t.ExternalTradeId}");
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, handler, "market", opts);
        var registry = new HandlerRegistry();
        registry.Register("market", typeof(TradeExecutedEvent));

        var resolver = new InboxStoreResolver();
        resolver.Register("*", store);

        var services = new ServiceCollection();
        services.AddSingleton<IInboxHandler<TradeExecutedEvent>>(handler);
        var sp = services.BuildServiceProvider();

        var processor = new InboxProcessor(resolver, registry, sp, opts,
            NullLogger<InboxProcessor>.Instance);

        return (store, receiver, processor, handler);
    }

    private static TradeExecutedEvent NewTrade(string id = "TRD-001") =>
        new("NYSE", id, "AAPL", 189.50m, 100, DateTime.UtcNow);

    [Fact]
    public async Task Processes_pending_message_and_calls_handler()
    {
        var (store, receiver, processor, handler) = Build();
        await receiver.ReceiveAsync(NewTrade());

        var result = await processor.ProcessPendingAsync("market");

        Assert.Equal(1, result.Processed);
        Assert.Equal(0, result.Failed);
        Assert.Single(handler.Handled);
    }

    [Fact]
    public async Task Marks_message_as_processed_in_store()
    {
        var (store, receiver, processor, _) = Build();
        await receiver.ReceiveAsync(NewTrade());

        await processor.ProcessPendingAsync("market");

        Assert.Equal(InboxMessageStatus.Processed, store.All.Single().Status);
        Assert.NotNull(store.All.Single().ProcessedAt);
    }

    [Fact]
    public async Task Does_not_reprocess_already_processed_message()
    {
        var (store, receiver, processor, handler) = Build();
        await receiver.ReceiveAsync(NewTrade());

        await processor.ProcessPendingAsync("market");
        await processor.ProcessPendingAsync("market"); // run again

        Assert.Equal(1, handler.CallCount); // still only once
    }

    [Fact]
    public async Task Processes_batch_of_messages()
    {
        var (store, receiver, processor, handler) = Build();

        for (var i = 1; i <= 10; i++)
            await receiver.ReceiveAsync(NewTrade($"TRD-{i:D3}"));

        var result = await processor.ProcessPendingAsync("market", batchSize: 50);

        Assert.Equal(10, result.Processed);
        Assert.Equal(10, handler.CallCount);
    }

    [Fact]
    public async Task Respects_batch_size_limit()
    {
        var (store, receiver, processor, handler) = Build();

        for (var i = 1; i <= 10; i++)
            await receiver.ReceiveAsync(NewTrade($"TRD-{i:D3}"));

        var result = await processor.ProcessPendingAsync("market", batchSize: 3);

        Assert.Equal(3, result.Processed);
        Assert.Equal(3, handler.CallCount);
        Assert.Equal(7, store.All.Count(m => m.Status == InboxMessageStatus.Pending));
    }

    [Fact]
    public async Task Failing_handler_increments_retry_count()
    {
        var store = new InMemoryInboxStore();
        var opts = new InboxOptions { MaxRetries = 5 };
        var failing = new AlwaysFailingHandler<TradeExecutedEvent>(t => $"trade:{t.ExternalTradeId}");
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, failing, "market", opts);
        var registry = new HandlerRegistry();
        registry.Register("market", typeof(TradeExecutedEvent));

        var resolver = new InboxStoreResolver();
        resolver.Register("*", store);

        var services = new ServiceCollection();
        services.AddSingleton<IInboxHandler<TradeExecutedEvent>>(failing);
        var sp = services.BuildServiceProvider();

        var processor = new InboxProcessor(resolver, registry, sp, opts,
            NullLogger<InboxProcessor>.Instance);

        await receiver.ReceiveAsync(NewTrade());
        await processor.ProcessPendingAsync("market");

        var msg = store.All.Single();
        Assert.Equal(InboxMessageStatus.Failed, msg.Status);
        Assert.Equal(1, msg.RetryCount);
        Assert.NotNull(msg.Error);
    }

    [Fact]
    public async Task Dead_letters_after_max_retries()
    {
        var deadLettered = new List<InboxMessage>();
        var store = new InMemoryInboxStore();
        var opts = new InboxOptions
        {
            MaxRetries = 3,
            OnDeadLettered = (msg, _) => { deadLettered.Add(msg); return Task.CompletedTask; }
        };
        var failing = new AlwaysFailingHandler<TradeExecutedEvent>(t => $"trade:{t.ExternalTradeId}");
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, failing, "market", opts);
        var registry = new HandlerRegistry();
        registry.Register("market", typeof(TradeExecutedEvent));

        var resolver = new InboxStoreResolver();
        resolver.Register("*", store);

        var services = new ServiceCollection();
        services.AddSingleton<IInboxHandler<TradeExecutedEvent>>(failing);
        var sp = services.BuildServiceProvider();
        var processor = new InboxProcessor(resolver, registry, sp, opts,
            NullLogger<InboxProcessor>.Instance);

        await receiver.ReceiveAsync(NewTrade());

        // Run until dead-lettered
        for (var i = 0; i < opts.MaxRetries; i++)
            await processor.ProcessPendingAsync("market");

        Assert.Equal(InboxMessageStatus.DeadLettered, store.All.Single().Status);
        Assert.Single(deadLettered); // hook was called
    }

    [Fact]
    public async Task Requeue_resets_dead_lettered_message_to_pending()
    {
        var store = new InMemoryInboxStore();
        var opts = new InboxOptions { MaxRetries = 1 };
        var failing = new AlwaysFailingHandler<TradeExecutedEvent>(t => $"trade:{t.ExternalTradeId}");
        var receiver = new InboxReceiver<TradeExecutedEvent>(store, failing, "market", opts);
        var registry = new HandlerRegistry();
        registry.Register("market", typeof(TradeExecutedEvent));

        var reqResolver = new InboxStoreResolver();
        reqResolver.Register("*", store);

        var services = new ServiceCollection();
        services.AddSingleton<IInboxHandler<TradeExecutedEvent>>(failing);
        var sp = services.BuildServiceProvider();
        var processor = new InboxProcessor(reqResolver, registry, sp, opts,
            NullLogger<InboxProcessor>.Instance);

        await receiver.ReceiveAsync(NewTrade());
        await processor.ProcessPendingAsync("market"); // → DeadLettered

        var id = store.All.Single().Id;
        var requeued = await store.RequeueAsync(id);

        Assert.True(requeued);
        Assert.Equal(InboxMessageStatus.Pending, store.All.Single().Status);
        Assert.Equal(0, store.All.Single().RetryCount);
    }

    [Fact]
    public async Task Stats_reflect_correct_counts()
    {
        var (store, receiver, processor, _) = Build();

        await receiver.ReceiveAsync(NewTrade("TRD-001")); // will be processed
        await receiver.ReceiveAsync(NewTrade("TRD-002")); // will stay pending

        await processor.ProcessPendingAsync("market", batchSize: 1); // only process one

        var stats = await store.GetStatsAsync("market");

        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Processed);
        Assert.NotNull(stats.OldestPending);
    }

    [Fact]
    public async Task Purge_removes_old_processed_messages()
    {
        var (store, receiver, processor, _) = Build();
        await receiver.ReceiveAsync(NewTrade());
        await processor.ProcessPendingAsync("market");

        // Backdate the processed message so it's older than cutoff
        store.All.Single().ProcessedAt = DateTime.UtcNow.AddDays(-8);

        var purged = await store.PurgeProcessedAsync("market", TimeSpan.FromDays(7));

        Assert.Equal(1, purged);
        Assert.Empty(store.All);
    }
}

// ---------------------------------------------------------------------------

public class InboxIsolationTests
{
    [Fact]
    public async Task Two_inboxes_are_fully_isolated()
    {
        var store = new InMemoryInboxStore();
        var opts = new InboxOptions();
        var tradeHandler = new FakeInboxHandler<TradeExecutedEvent>(
            t => $"trade:{t.Exchange}:{t.ExternalTradeId}");
        var orderHandler = new FakeInboxHandler<OrderFilledEvent>(
            o => $"order-fill:{o.Exchange}:{o.OrderId}");

        var marketReceiver = new InboxReceiver<TradeExecutedEvent>(store, tradeHandler, "market", opts);
        var paymentReceiver = new InboxReceiver<OrderFilledEvent>(store, orderHandler, "payments", opts);

        await marketReceiver.ReceiveAsync(new TradeExecutedEvent("NYSE", "TRD-001", "AAPL", 1m, 1, DateTime.UtcNow));
        await paymentReceiver.ReceiveAsync(new OrderFilledEvent("NYSE", "ORD-001", "AAPL", 1m));

        var marketMessages = store.All.Where(m => m.InboxName == "market").ToList();
        var paymentMessages = store.All.Where(m => m.InboxName == "payments").ToList();

        Assert.Single(marketMessages);
        Assert.Single(paymentMessages);
    }
}
