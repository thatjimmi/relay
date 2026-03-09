using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Inbox.Extensions;
using Relay.Inbox.Storage;
using Relay.Outbox.Core;
using Relay.Outbox.Extensions;
using Relay.Outbox.Internal;
using Relay.Outbox.Storage;

namespace Relay.Sample.Trayport.Tests;

// ---------------------------------------------------------------------------
// Domain types — same shape as Relay.Sample.Trayport.Domain
// (kept local to avoid referencing the Azure Functions host project)
// ---------------------------------------------------------------------------

public record TrayportTrade(
    string TradeId,
    string Commodity,
    string Counterparty,
    decimal Price,
    decimal Volume,
    string Unit,
    DateTime TradeDate,
    string Direction);

public record TradeDto(
    string InternalTradeId,
    string Commodity,
    string CounterpartyCode,
    decimal Price,
    decimal Volume,
    string Unit,
    DateOnly TradeDate,
    TradeDirection Direction);

public enum TradeDirection { Buy, Sell }

public record RiskTradeEvent(
    string TradeId,
    string Commodity,
    decimal Notional,
    TradeDirection Direction);

public record SettlementRequest(
    string TradeId,
    string CounterpartyCode,
    decimal Amount,
    DateOnly SettleDate);

// ---------------------------------------------------------------------------
// Mapper — same logic as Relay.Sample.Trayport.Domain.TradeMapper
// ---------------------------------------------------------------------------

public static class TradeMapper
{
    public static TradeDto Map(TrayportTrade t) => new(
        InternalTradeId: $"TPT-{t.TradeId}",
        Commodity: t.Commodity,
        CounterpartyCode: NormaliseCounterparty(t.Counterparty),
        Price: t.Price,
        Volume: t.Volume,
        Unit: t.Unit,
        TradeDate: DateOnly.FromDateTime(t.TradeDate),
        Direction: Enum.Parse<TradeDirection>(t.Direction, ignoreCase: true));

    public static RiskTradeEvent ToRiskEvent(TradeDto dto) => new(
        dto.InternalTradeId,
        dto.Commodity,
        dto.Price * dto.Volume,
        dto.Direction);

    public static SettlementRequest ToSettlement(TradeDto dto) => new(
        dto.InternalTradeId,
        dto.CounterpartyCode,
        dto.Price * dto.Volume,
        dto.TradeDate.AddDays(2));

    public static string NormaliseCounterparty(string name) =>
        name.Trim().ToUpperInvariant().Replace(" ", "_");
}

// ===========================================================================
// Tests — exercises the full IInboxClient + IOutboxWriter flow used by the
// Trayport Azure Functions sample: receive → map → fan-out → dispatch.
// ===========================================================================

public class TrayportTradeMapperTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private static TrayportTrade SampleTrade(string id = "TRD-001") => new(
        id, "Power", "Shell Energy", 85.50m, 100m, "MWh", Now, "Buy");

    // ── Mapper ─────────────────────────────────────────────────────────────

    [Fact]
    public void Map_produces_correct_internal_id()
    {
        var dto = TradeMapper.Map(SampleTrade());
        Assert.Equal("TPT-TRD-001", dto.InternalTradeId);
    }

    [Fact]
    public void Map_normalises_counterparty()
    {
        var dto = TradeMapper.Map(SampleTrade());
        Assert.Equal("SHELL_ENERGY", dto.CounterpartyCode);
    }

    [Fact]
    public void Map_parses_direction()
    {
        var buy = TradeMapper.Map(SampleTrade() with { Direction = "Buy" });
        var sell = TradeMapper.Map(SampleTrade() with { Direction = "sell" });

        Assert.Equal(TradeDirection.Buy, buy.Direction);
        Assert.Equal(TradeDirection.Sell, sell.Direction);
    }

    [Fact]
    public void Map_converts_trade_date_to_date_only()
    {
        var dto = TradeMapper.Map(SampleTrade());
        Assert.Equal(DateOnly.FromDateTime(Now), dto.TradeDate);
    }

    [Fact]
    public void Map_preserves_price_and_volume()
    {
        var dto = TradeMapper.Map(SampleTrade());
        Assert.Equal(85.50m, dto.Price);
        Assert.Equal(100m, dto.Volume);
        Assert.Equal("MWh", dto.Unit);
        Assert.Equal("Power", dto.Commodity);
    }

    [Fact]
    public void ToRiskEvent_calculates_notional()
    {
        var dto = TradeMapper.Map(SampleTrade());
        var risk = TradeMapper.ToRiskEvent(dto);

        Assert.Equal("TPT-TRD-001", risk.TradeId);
        Assert.Equal(85.50m * 100m, risk.Notional);
        Assert.Equal(TradeDirection.Buy, risk.Direction);
        Assert.Equal("Power", risk.Commodity);
    }

    [Fact]
    public void ToSettlement_calculates_t_plus_2()
    {
        var dto = TradeMapper.Map(SampleTrade());
        var settlement = TradeMapper.ToSettlement(dto);

        Assert.Equal("TPT-TRD-001", settlement.TradeId);
        Assert.Equal("SHELL_ENERGY", settlement.CounterpartyCode);
        Assert.Equal(85.50m * 100m, settlement.Amount);
        Assert.Equal(DateOnly.FromDateTime(Now).AddDays(2), settlement.SettleDate);
    }

    [Theory]
    [InlineData("Shell Energy", "SHELL_ENERGY")]
    [InlineData("  BP Trading  ", "BP_TRADING")]
    [InlineData("vitol", "VITOL")]
    [InlineData("Total Energies SE", "TOTAL_ENERGIES_SE")]
    public void NormaliseCounterparty_handles_various_inputs(string input, string expected)
    {
        Assert.Equal(expected, TradeMapper.NormaliseCounterparty(input));
    }
}

public class TrayportInboxTests
{
    private const string Channel = "trades";
    private static readonly DateTime Now = DateTime.UtcNow;

    private static TrayportTrade SampleTrade(string id = "TRD-001") => new(
        id, "Power", "Shell Energy", 85.50m, 100m, "MWh", Now, "Buy");

    private static (InMemoryInboxStore inboxStore, IInboxClient client) BuildInbox(
        Action<InboxOptions>? configure = null)
    {
        var store = new InMemoryInboxStore();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInboxClient(configure);
        services.UseInboxStore(store);

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IInboxClient>();

        return (store, client);
    }

    private static (InMemoryInboxStore inboxStore, IInboxClient client,
                     InMemoryOutboxStore outboxStore, IOutboxWriter writer,
                     IOutboxDispatcher dispatcher) BuildFull()
    {
        var inboxStore = new InMemoryInboxStore();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInboxClient();
        services.UseInboxStore(inboxStore);

        // Register publishers in a named outbox, use global in-memory store.
        services.AddOutbox(Channel, outbox => outbox
            .WithPublisher<RiskTradeEvent, FakeRiskPublisher>()
            .WithPublisher<SettlementRequest, FakeSettlementPublisher>());
        services.UseInMemoryOutboxStore();

        var sp = services.BuildServiceProvider();
        return (
            inboxStore,
            sp.GetRequiredService<IInboxClient>(),
            sp.GetRequiredService<InMemoryOutboxStore>(),
            sp.GetRequiredService<IOutboxWriter>(),
            sp.GetRequiredService<IOutboxDispatcher>());
    }

    // ── Receive ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Receive_trade_stores_message()
    {
        var (store, client) = BuildInbox();
        var trade = SampleTrade();

        var result = await client.ReceiveAsync(
            Channel, trade, $"trade:{trade.TradeId}", "trayport-api", sourceTimestamp: trade.TradeDate);

        Assert.True(result.Accepted);
        Assert.False(result.WasDuplicate);
        Assert.Single(store.All);
        Assert.Equal(nameof(TrayportTrade), store.All[0].Type);
        Assert.Equal(Channel, store.All[0].InboxName);
    }

    [Fact]
    public async Task Receive_duplicate_is_idempotent()
    {
        var (store, client) = BuildInbox();
        var trade = SampleTrade();

        await client.ReceiveAsync(Channel, trade, $"trade:{trade.TradeId}", "trayport-api");
        var dup = await client.ReceiveAsync(Channel, trade, $"trade:{trade.TradeId}", "trayport-api");

        Assert.True(dup.WasDuplicate);
        Assert.Single(store.All);
    }

    [Fact]
    public async Task Receive_amendment_with_newer_timestamp_updates_payload()
    {
        var (store, client) = BuildInbox();
        var original = SampleTrade() with { Price = 80.00m, TradeDate = Now.AddHours(-1) };
        var amendment = SampleTrade() with { Price = 90.00m, TradeDate = Now };

        await client.ReceiveAsync(
            Channel, original, $"trade:{original.TradeId}", "trayport-api",
            sourceTimestamp: original.TradeDate);

        var result = await client.ReceiveAsync(
            Channel, amendment, $"trade:{amendment.TradeId}", "trayport-api",
            sourceTimestamp: amendment.TradeDate);

        Assert.True(result.WasUpdated);
        Assert.Single(store.All);

        // Verify payload was actually updated
        var stored = JsonSerializer.Deserialize<TrayportTrade>(store.All[0].Payload)!;
        Assert.Equal(90.00m, stored.Price);
    }

    [Fact]
    public async Task Receive_amendment_with_older_timestamp_is_ignored()
    {
        var (store, client) = BuildInbox();
        var original = SampleTrade() with { Price = 80.00m, TradeDate = Now };
        var stale = SampleTrade() with { Price = 70.00m, TradeDate = Now.AddHours(-1) };

        await client.ReceiveAsync(
            Channel, original, $"trade:{original.TradeId}", "trayport-api",
            sourceTimestamp: original.TradeDate);

        var result = await client.ReceiveAsync(
            Channel, stale, $"trade:{stale.TradeId}", "trayport-api",
            sourceTimestamp: stale.TradeDate);

        Assert.True(result.WasDuplicate);
        Assert.Single(store.All);

        var stored = JsonSerializer.Deserialize<TrayportTrade>(store.All[0].Payload)!;
        Assert.Equal(80.00m, stored.Price); // Original price preserved
    }

    // ── Manual processing ──────────────────────────────────────────────────

    [Fact]
    public async Task GetPending_returns_only_pending_trades()
    {
        var (store, client) = BuildInbox();

        await client.ReceiveAsync(Channel, SampleTrade("TRD-001"), "trade:TRD-001", "api");
        await client.ReceiveAsync(Channel, SampleTrade("TRD-002"), "trade:TRD-002", "api");

        var pending = await client.GetPendingAsync(Channel, batchSize: 10);
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public async Task MarkProcessed_removes_from_pending()
    {
        var (store, client) = BuildInbox();

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "api");
        var pending = await client.GetPendingAsync(Channel, batchSize: 10);
        Assert.Single(pending);

        await client.MarkProcessedAsync(pending[0]);

        var remaining = await client.GetPendingAsync(Channel, batchSize: 10);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task MarkFailed_increments_retry_count()
    {
        var (store, client) = BuildInbox();

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "api");
        var pending = await client.GetPendingAsync(Channel, batchSize: 10);

        await client.MarkFailedAsync(pending[0], "Connection timeout", maxRetries: 5);

        // Message should still be pending (for retry)
        var msg = store.All[0];
        Assert.Equal(1, msg.RetryCount);
        Assert.Equal("Connection timeout", msg.Error);
    }

    [Fact]
    public async Task MarkFailed_dead_letters_after_max_retries()
    {
        var (store, client) = BuildInbox();

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "api");

        for (int i = 0; i < 3; i++)
        {
            var pending = await client.GetPendingAsync(Channel, batchSize: 10);
            if (pending.Count == 0) break;
            await client.MarkFailedAsync(pending[0], $"Failure {i + 1}", maxRetries: 3);
        }

        var msg = store.All[0];
        Assert.Equal(InboxMessageStatus.DeadLettered, msg.Status);
    }

    // ── Map → fan-out flow ─────────────────────────────────────────────────

    [Fact]
    public async Task Process_trade_maps_and_writes_outbox_messages()
    {
        var (inboxStore, client, outboxStore, writer, _) = BuildFull();

        // 1. Receive a trade
        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "trayport-api");

        // 2. Get pending and process manually (simulating ProcessTradesFunction)
        var pending = await client.GetPendingAsync(Channel, batchSize: 10);
        Assert.Single(pending);

        var msg = pending[0];
        var raw = JsonSerializer.Deserialize<TrayportTrade>(msg.Payload)!;
        var dto = TradeMapper.Map(raw);

        // Set correlation context manually (as the timer function does)
        using (OutboxCorrelationContext.Set(msg.Id.ToString()))
        {
            await writer.WriteAsync(TradeMapper.ToRiskEvent(dto), Channel, "risk.trades");
            await writer.WriteAsync(TradeMapper.ToSettlement(dto), Channel, "settlement.requests");
        }

        await client.MarkProcessedAsync(msg);

        // 3. Verify outbox messages
        Assert.Equal(2, outboxStore.All.Count);

        var riskMsg = outboxStore.All.First(m => m.Type == nameof(RiskTradeEvent));
        Assert.Equal("risk.trades", riskMsg.Destination);
        Assert.Equal(msg.Id.ToString(), riskMsg.CorrelationId);

        var settlementMsg = outboxStore.All.First(m => m.Type == nameof(SettlementRequest));
        Assert.Equal("settlement.requests", settlementMsg.Destination);
        Assert.Equal(msg.Id.ToString(), settlementMsg.CorrelationId);

        // 4. Verify inbox message is processed
        var remainingPending = await client.GetPendingAsync(Channel, batchSize: 10);
        Assert.Empty(remainingPending);
    }

    [Fact]
    public async Task Process_multiple_trades_produces_correct_outbox_messages()
    {
        var (inboxStore, client, outboxStore, writer, _) = BuildFull();

        // Receive 3 trades
        await client.ReceiveAsync(Channel, SampleTrade("TRD-001"), "trade:TRD-001", "api");
        await client.ReceiveAsync(Channel,
            new TrayportTrade("TRD-002", "Gas", "BP Trading", 42.75m, 500m, "therm", Now, "Sell"),
            "trade:TRD-002", "api");
        await client.ReceiveAsync(Channel,
            new TrayportTrade("TRD-003", "Carbon", "Vitol SA", 78.20m, 1000m, "tCO2", Now, "Buy"),
            "trade:TRD-003", "api");

        // Process all
        var pending = await client.GetPendingAsync(Channel, batchSize: 50);
        Assert.Equal(3, pending.Count);

        foreach (var msg in pending)
        {
            var raw = JsonSerializer.Deserialize<TrayportTrade>(msg.Payload)!;
            var dto = TradeMapper.Map(raw);

            using (OutboxCorrelationContext.Set(msg.Id.ToString()))
            {
                await writer.WriteAsync(TradeMapper.ToRiskEvent(dto), Channel, "risk.trades");
                await writer.WriteAsync(TradeMapper.ToSettlement(dto), Channel, "settlement.requests");
            }

            await client.MarkProcessedAsync(msg);
        }

        // 3 trades × 2 outbox messages each = 6
        Assert.Equal(6, outboxStore.All.Count);
        Assert.Equal(3, outboxStore.All.Count(m => m.Type == nameof(RiskTradeEvent)));
        Assert.Equal(3, outboxStore.All.Count(m => m.Type == nameof(SettlementRequest)));

        // All inbox messages processed
        var remaining = await client.GetPendingAsync(Channel, batchSize: 10);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Outbox_messages_contain_correct_payloads()
    {
        var (_, client, outboxStore, writer, _) = BuildFull();

        var trade = SampleTrade();
        await client.ReceiveAsync(Channel, trade, "trade:TRD-001", "api");

        var pending = await client.GetPendingAsync(Channel, batchSize: 10);
        var msg = pending[0];
        var dto = TradeMapper.Map(JsonSerializer.Deserialize<TrayportTrade>(msg.Payload)!);

        using (OutboxCorrelationContext.Set(msg.Id.ToString()))
        {
            await writer.WriteAsync(TradeMapper.ToRiskEvent(dto), Channel, "risk.trades");
            await writer.WriteAsync(TradeMapper.ToSettlement(dto), Channel, "settlement.requests");
        }

        // Verify risk event payload
        var riskEnvelope = outboxStore.All.First(m => m.Type == nameof(RiskTradeEvent));
        var riskEvent = JsonSerializer.Deserialize<RiskTradeEvent>(riskEnvelope.Payload)!;
        Assert.Equal("TPT-TRD-001", riskEvent.TradeId);
        Assert.Equal(85.50m * 100m, riskEvent.Notional);
        Assert.Equal(TradeDirection.Buy, riskEvent.Direction);

        // Verify settlement payload
        var settleEnvelope = outboxStore.All.First(m => m.Type == nameof(SettlementRequest));
        var settlement = JsonSerializer.Deserialize<SettlementRequest>(settleEnvelope.Payload)!;
        Assert.Equal("TPT-TRD-001", settlement.TradeId);
        Assert.Equal("SHELL_ENERGY", settlement.CounterpartyCode);
        Assert.Equal(85.50m * 100m, settlement.Amount);
    }

    // ── Hooks ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnMessageStored_hook_fires_on_receive()
    {
        var stored = new List<InboxMessage>();
        var (_, client) = BuildInbox(o => o.OnMessageStored = msg =>
        {
            stored.Add(msg);
            return Task.CompletedTask;
        });

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "trayport-api");

        Assert.Single(stored);
        Assert.Equal(nameof(TrayportTrade), stored[0].Type);
    }

    [Fact]
    public async Task OnDeadLettered_hook_fires_when_retries_exhausted()
    {
        var deadLettered = new List<InboxMessage>();
        var (_, client) = BuildInbox(o => o.OnDeadLettered = (msg, ex) =>
        {
            deadLettered.Add(msg);
            return Task.CompletedTask;
        });

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "api");

        for (int i = 0; i < 2; i++)
        {
            var pending = await client.GetPendingAsync(Channel, batchSize: 10);
            if (pending.Count == 0) break;
            await client.MarkFailedAsync(pending[0], "Error", maxRetries: 2);
        }

        Assert.Single(deadLettered);
    }

    [Fact]
    public async Task OnProcessed_hook_fires_on_mark_processed()
    {
        var processed = new List<InboxMessage>();
        var (_, client) = BuildInbox(o => o.OnProcessed = msg =>
        {
            processed.Add(msg);
            return Task.CompletedTask;
        });

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "api");
        var pending = await client.GetPendingAsync(Channel, batchSize: 10);
        await client.MarkProcessedAsync(pending[0]);

        Assert.Single(processed);
    }

    // ── Stats + requeue ────────────────────────────────────────────────────

    [Fact]
    public async Task Stats_reflect_inbox_state()
    {
        var (store, client) = BuildInbox();

        await client.ReceiveAsync(Channel, SampleTrade("TRD-001"), "trade:TRD-001", "api");
        await client.ReceiveAsync(Channel, SampleTrade("TRD-002"), "trade:TRD-002", "api");

        var pending = await client.GetPendingAsync(Channel, batchSize: 1);
        await client.MarkProcessedAsync(pending[0]);

        var stats = await store.GetStatsAsync(Channel);
        Assert.Equal(1, stats.Pending);
        Assert.Equal(1, stats.Processed);
    }

    [Fact]
    public async Task Requeue_dead_lettered_message_makes_it_pending_again()
    {
        var (store, client) = BuildInbox();

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "api");

        // Dead-letter it
        for (int i = 0; i < 2; i++)
        {
            var p = await client.GetPendingAsync(Channel, batchSize: 10);
            if (p.Count == 0) break;
            await client.MarkFailedAsync(p[0], "Error", maxRetries: 2);
        }

        Assert.Equal(InboxMessageStatus.DeadLettered, store.All[0].Status);

        // Requeue
        var requeued = await store.RequeueAsync(store.All[0].Id);
        Assert.True(requeued);
        Assert.Equal(InboxMessageStatus.Pending, store.All[0].Status);

        var pending = await client.GetPendingAsync(Channel, batchSize: 10);
        Assert.Single(pending);
    }

    // ── Correlation ────────────────────────────────────────────────────────

    [Fact]
    public async Task Outbox_messages_are_correlated_to_inbox_message()
    {
        var (_, client, outboxStore, writer, _) = BuildFull();

        await client.ReceiveAsync(Channel, SampleTrade(), "trade:TRD-001", "api");
        var pending = await client.GetPendingAsync(Channel, batchSize: 10);
        var inboxMsg = pending[0];

        using (OutboxCorrelationContext.Set(inboxMsg.Id.ToString()))
        {
            var dto = TradeMapper.Map(
                JsonSerializer.Deserialize<TrayportTrade>(inboxMsg.Payload)!);
            await writer.WriteAsync(TradeMapper.ToRiskEvent(dto), Channel);
            await writer.WriteAsync(TradeMapper.ToSettlement(dto), Channel);
        }

        // Both outbox messages should have the inbox message ID as correlation
        Assert.All(outboxStore.All, m =>
            Assert.Equal(inboxMsg.Id.ToString(), m.CorrelationId));

        // Verify by query
        var correlated = await outboxStore.GetByCorrelationIdAsync(inboxMsg.Id.ToString());
        Assert.Equal(2, correlated.Count);
    }

    [Fact]
    public async Task Correlation_context_does_not_leak_between_messages()
    {
        var (_, client, outboxStore, writer, _) = BuildFull();

        await client.ReceiveAsync(Channel, SampleTrade("TRD-001"), "trade:TRD-001", "api");
        await client.ReceiveAsync(Channel, SampleTrade("TRD-002"), "trade:TRD-002", "api");

        var pending = await client.GetPendingAsync(Channel, batchSize: 10);

        foreach (var msg in pending)
        {
            var dto = TradeMapper.Map(
                JsonSerializer.Deserialize<TrayportTrade>(msg.Payload)!);

            using (OutboxCorrelationContext.Set(msg.Id.ToString()))
            {
                await writer.WriteAsync(TradeMapper.ToRiskEvent(dto), Channel);
            }
        }

        // Each outbox message should have its own inbox message ID
        var correlationIds = outboxStore.All.Select(m => m.CorrelationId).Distinct().ToList();
        Assert.Equal(2, correlationIds.Count);
    }
}

// ---------------------------------------------------------------------------
// Fake publishers for test DI
// ---------------------------------------------------------------------------

public sealed class FakeRiskPublisher : IOutboxPublisher<RiskTradeEvent>
{
    public List<RiskTradeEvent> Published { get; } = [];

    public Task PublishAsync(RiskTradeEvent message, OutboxMessage envelope, CancellationToken ct = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}

public sealed class FakeSettlementPublisher : IOutboxPublisher<SettlementRequest>
{
    public List<SettlementRequest> Published { get; } = [];

    public Task PublishAsync(SettlementRequest message, OutboxMessage envelope, CancellationToken ct = default)
    {
        Published.Add(message);
        return Task.CompletedTask;
    }
}
