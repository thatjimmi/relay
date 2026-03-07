using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Relay.Outbox.Core;
using Relay.Outbox.Internal;
using Relay.Outbox.Storage;
using Relay.Outbox.Testing;

namespace Relay.Outbox.Tests;

// ---------------------------------------------------------------------------
// Sample domain events
// ---------------------------------------------------------------------------

public record TradeConfirmedEvent(string Exchange, string TradeId, string Symbol, decimal Price);
public record PositionUpdatedEvent(string AccountId, string Symbol, decimal NewQuantity);

// ---------------------------------------------------------------------------

public class OutboxWriterTests
{
    private static (InMemoryOutboxStore store, OutboxWriter writer) Build()
    {
        var store  = new InMemoryOutboxStore();
        var writer = new OutboxWriter(store);
        return (store, writer);
    }

    [Fact]
    public async Task Write_stores_message_as_pending()
    {
        var (store, writer) = Build();

        var result = await writer.WriteAsync(
            new TradeConfirmedEvent("NYSE", "TRD-001", "AAPL", 189.50m),
            "market-exchange");

        Assert.Single(store.All);
        Assert.Equal(OutboxMessageStatus.Pending, store.All[0].Status);
        Assert.Equal(result.MessageId, store.All[0].Id);
    }

    [Fact]
    public async Task Write_captures_type_name_and_payload()
    {
        var (store, writer) = Build();

        await writer.WriteAsync(
            new TradeConfirmedEvent("NYSE", "TRD-001", "AAPL", 189.50m),
            "market-exchange");

        var msg = store.All[0];
        Assert.Equal(nameof(TradeConfirmedEvent), msg.Type);
        Assert.Contains("TRD-001", msg.Payload);
    }

    [Fact]
    public async Task Write_with_destination_stores_destination()
    {
        var (store, writer) = Build();

        await writer.WriteAsync(
            new TradeConfirmedEvent("NYSE", "TRD-001", "AAPL", 189.50m),
            "market-exchange",
            destination: "trades.confirmed");

        Assert.Equal("trades.confirmed", store.All[0].Destination);
    }

    [Fact]
    public async Task WriteScheduled_stores_future_timestamp()
    {
        var (store, writer) = Build();
        var future = DateTime.UtcNow.AddMinutes(30);

        var result = await writer.WriteScheduledAsync(
            new TradeConfirmedEvent("NYSE", "TRD-001", "AAPL", 189.50m),
            "market-exchange",
            scheduledFor: future);

        Assert.True(result.IsScheduled);
        Assert.NotNull(store.All[0].ScheduledFor);
        Assert.True(store.All[0].ScheduledFor >= future.AddSeconds(-1));
    }

    [Fact]
    public async Task WriteScheduled_message_not_returned_in_pending_until_due()
    {
        var (store, writer) = Build();
        var future = DateTime.UtcNow.AddHours(1);

        await writer.WriteScheduledAsync(
            new TradeConfirmedEvent("NYSE", "TRD-001", "AAPL", 189.50m),
            "market-exchange",
            scheduledFor: future);

        var pending = await store.GetPendingAsync("market-exchange", 50);
        Assert.Empty(pending); // not due yet
    }

    [Fact]
    public async Task Correlation_context_is_captured_when_set()
    {
        var (store, writer) = Build();

        using (OutboxCorrelationContext.Set("inbox-msg-abc-123"))
        {
            await writer.WriteAsync(
                new TradeConfirmedEvent("NYSE", "TRD-001", "AAPL", 189.50m),
                "market-exchange");
        }

        Assert.Equal("inbox-msg-abc-123", store.All[0].CorrelationId);
    }

    [Fact]
    public async Task Correlation_context_cleared_after_scope_disposed()
    {
        var (store, writer) = Build();

        using (OutboxCorrelationContext.Set("scope-1"))
        {
            // inside scope
        }

        await writer.WriteAsync(
            new TradeConfirmedEvent("NYSE", "TRD-002", "AAPL", 189.50m),
            "market-exchange");

        Assert.Null(store.All[0].CorrelationId); // no correlation outside scope
    }
}

// ---------------------------------------------------------------------------

public class OutboxDispatcherTests
{
    private static (InMemoryOutboxStore store, OutboxWriter writer,
                    OutboxDispatcher dispatcher, FakeOutboxPublisher<TradeConfirmedEvent> publisher)
        Build(OutboxOptions? options = null)
    {
        var store     = new InMemoryOutboxStore();
        var writer    = new OutboxWriter(store);
        var opts      = options ?? new OutboxOptions();
        var publisher = new FakeOutboxPublisher<TradeConfirmedEvent>();
        var registry  = new PublisherRegistry();
        registry.Register("market-exchange", typeof(TradeConfirmedEvent));

        var services = new ServiceCollection();
        services.AddSingleton<IOutboxPublisher<TradeConfirmedEvent>>(publisher);
        var sp = services.BuildServiceProvider();

        var dispatcher = new OutboxDispatcher(store, registry, sp, opts,
            NullLogger<OutboxDispatcher>.Instance);

        return (store, writer, dispatcher, publisher);
    }

    private TradeConfirmedEvent NewEvent(string id = "TRD-001") =>
        new("NYSE", id, "AAPL", 189.50m);

    [Fact]
    public async Task Dispatches_pending_message_to_publisher()
    {
        var (store, writer, dispatcher, publisher) = Build();
        await writer.WriteAsync(NewEvent(), "market-exchange");

        var result = await dispatcher.DispatchPendingAsync("market-exchange");

        Assert.Equal(1, result.Published);
        Assert.Single(publisher.Messages);
        Assert.Equal("TRD-001", publisher.Messages[0].TradeId);
    }

    [Fact]
    public async Task Marks_message_as_published_after_dispatch()
    {
        var (store, writer, dispatcher, _) = Build();
        await writer.WriteAsync(NewEvent(), "market-exchange");

        await dispatcher.DispatchPendingAsync("market-exchange");

        Assert.Equal(OutboxMessageStatus.Published, store.All[0].Status);
        Assert.NotNull(store.All[0].PublishedAt);
    }

    [Fact]
    public async Task Does_not_redispatch_already_published_message()
    {
        var (store, writer, dispatcher, publisher) = Build();
        await writer.WriteAsync(NewEvent(), "market-exchange");

        await dispatcher.DispatchPendingAsync("market-exchange");
        await dispatcher.DispatchPendingAsync("market-exchange"); // run again

        Assert.Equal(1, publisher.CallCount);
    }

    [Fact]
    public async Task Dispatches_batch_of_messages()
    {
        var (store, writer, dispatcher, publisher) = Build();
        for (var i = 1; i <= 10; i++)
            await writer.WriteAsync(NewEvent($"TRD-{i:D3}"), "market-exchange");

        var result = await dispatcher.DispatchPendingAsync("market-exchange", batchSize: 50);

        Assert.Equal(10, result.Published);
        Assert.Equal(10, publisher.CallCount);
    }

    [Fact]
    public async Task Respects_batch_size()
    {
        var (store, writer, dispatcher, publisher) = Build();
        for (var i = 1; i <= 10; i++)
            await writer.WriteAsync(NewEvent($"TRD-{i:D3}"), "market-exchange");

        var result = await dispatcher.DispatchPendingAsync("market-exchange", batchSize: 3);

        Assert.Equal(3, result.Published);
        Assert.Equal(7, store.All.Count(m => m.Status == OutboxMessageStatus.Pending));
    }

    [Fact]
    public async Task Failed_publisher_increments_retry_count()
    {
        var store     = new InMemoryOutboxStore();
        var writer    = new OutboxWriter(store);
        var opts      = new OutboxOptions { MaxRetries = 5 };
        var failing   = new AlwaysFailingPublisher<TradeConfirmedEvent>();
        var registry  = new PublisherRegistry();
        registry.Register("market-exchange", typeof(TradeConfirmedEvent));
        var services  = new ServiceCollection();
        services.AddSingleton<IOutboxPublisher<TradeConfirmedEvent>>(failing);
        var dispatcher = new OutboxDispatcher(store, registry, services.BuildServiceProvider(),
            opts, NullLogger<OutboxDispatcher>.Instance);

        await writer.WriteAsync(NewEvent(), "market-exchange");
        await dispatcher.DispatchPendingAsync("market-exchange");

        Assert.Equal(OutboxMessageStatus.Failed, store.All[0].Status);
        Assert.Equal(1, store.All[0].RetryCount);
    }

    [Fact]
    public async Task Dead_letters_after_max_retries()
    {
        var deadLettered = new List<OutboxMessage>();
        var store     = new InMemoryOutboxStore();
        var writer    = new OutboxWriter(store);
        var opts      = new OutboxOptions
        {
            MaxRetries = 3,
            OnDeadLettered = (msg, _) => { deadLettered.Add(msg); return Task.CompletedTask; }
        };
        var failing   = new AlwaysFailingPublisher<TradeConfirmedEvent>();
        var registry  = new PublisherRegistry();
        registry.Register("market-exchange", typeof(TradeConfirmedEvent));
        var services  = new ServiceCollection();
        services.AddSingleton<IOutboxPublisher<TradeConfirmedEvent>>(failing);
        var dispatcher = new OutboxDispatcher(store, registry, services.BuildServiceProvider(),
            opts, NullLogger<OutboxDispatcher>.Instance);

        await writer.WriteAsync(NewEvent(), "market-exchange");
        for (var i = 0; i < opts.MaxRetries; i++)
            await dispatcher.DispatchPendingAsync("market-exchange");

        Assert.Equal(OutboxMessageStatus.DeadLettered, store.All[0].Status);
        Assert.Single(deadLettered);
    }

    [Fact]
    public async Task Requeue_resets_dead_lettered_to_pending()
    {
        var store     = new InMemoryOutboxStore();
        var writer    = new OutboxWriter(store);
        var opts      = new OutboxOptions { MaxRetries = 1 };
        var failing   = new AlwaysFailingPublisher<TradeConfirmedEvent>();
        var registry  = new PublisherRegistry();
        registry.Register("market-exchange", typeof(TradeConfirmedEvent));
        var services  = new ServiceCollection();
        services.AddSingleton<IOutboxPublisher<TradeConfirmedEvent>>(failing);
        var dispatcher = new OutboxDispatcher(store, registry, services.BuildServiceProvider(),
            opts, NullLogger<OutboxDispatcher>.Instance);

        await writer.WriteAsync(NewEvent(), "market-exchange");
        await dispatcher.DispatchPendingAsync("market-exchange"); // → DeadLettered

        var id = store.All[0].Id;
        var requeued = await store.RequeueAsync(id);

        Assert.True(requeued);
        Assert.Equal(OutboxMessageStatus.Pending, store.All[0].Status);
        Assert.Equal(0, store.All[0].RetryCount);
    }

    [Fact]
    public async Task GetByCorrelationId_returns_linked_messages()
    {
        var (store, writer, dispatcher, publisher) = Build();

        using (OutboxCorrelationContext.Set("inbox-abc"))
        {
            await writer.WriteAsync(NewEvent("TRD-001"), "market-exchange");
            await writer.WriteAsync(NewEvent("TRD-002"), "market-exchange");
        }
        await writer.WriteAsync(NewEvent("TRD-003"), "market-exchange"); // no correlation

        var correlated = await store.GetByCorrelationIdAsync("inbox-abc");
        Assert.Equal(2, correlated.Count);
        Assert.All(correlated, m => Assert.Equal("inbox-abc", m.CorrelationId));
    }

    [Fact]
    public async Task Stats_reflect_correct_counts()
    {
        var (store, writer, dispatcher, _) = Build();
        await writer.WriteAsync(NewEvent("TRD-001"), "market-exchange");
        await writer.WriteAsync(NewEvent("TRD-002"), "market-exchange");

        await dispatcher.DispatchPendingAsync("market-exchange", batchSize: 1);

        var stats = await store.GetStatsAsync("market-exchange");
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Published);
    }

    [Fact]
    public async Task Purge_removes_old_published_messages()
    {
        var (store, writer, dispatcher, _) = Build();
        await writer.WriteAsync(NewEvent(), "market-exchange");
        await dispatcher.DispatchPendingAsync("market-exchange");

        store.All[0].PublishedAt = DateTime.UtcNow.AddDays(-8);

        var purged = await store.PurgePublishedAsync("market-exchange", TimeSpan.FromDays(7));
        Assert.Equal(1, purged);
        Assert.Empty(store.All);
    }
}
