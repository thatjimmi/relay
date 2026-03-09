using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Relay.Inbox.Core;
using Relay.Outbox.Core;
using Relay.Outbox.Internal;
using Relay.Sample.Trayport.Domain;

namespace Relay.Sample.Trayport.Functions;

/// <summary>
/// Timer Trigger — manually processes pending trade inbox messages.
///
/// This is the core of the "client mode" pattern:
///   1. Fetch pending messages via IInboxClient
///   2. Deserialize the raw TrayportTrade payload
///   3. Map to internal TradeDto via TradeMapper
///   4. Fan-out: write RiskTradeEvent + SettlementRequest to the outbox
///   5. Mark processed (or mark failed with retry limit)
///
/// OutboxCorrelationContext is set manually so outbox messages are correlated
/// back to the inbox message that caused them.
///
/// In production this would be a Service Bus trigger; we use a timer here
/// because we don't have a Service Bus instance.
///
/// IInboxClient + IOutboxWriter are scoped; Azure Functions creates a new
/// scope per invocation.
/// </summary>
public sealed class ProcessTradesFunction(
    IInboxClient inbox,
    IOutboxWriter outbox,
    ILogger<ProcessTradesFunction> logger)
{
    private const string Channel = "trades";
    private const string Schedule = "*/30 * * * * *";
    private const int MaxRetries = 5;

    [Function("ProcessTrades")]
    public async Task Run(
        [TimerTrigger(Schedule, RunOnStartup = true, UseMonitor = false)] TimerInfo timer,
        CancellationToken ct)
    {
        logger.LogInformation("ProcessTrades triggered — past-due: {PastDue}", timer.IsPastDue);

        var messages = await inbox.GetPendingAsync(Channel, batchSize: 50, ct);

        if (messages.Count == 0)
            return;

        var processed = 0;
        var failed = 0;

        foreach (var msg in messages)
        {
            try
            {
                if (msg.Type != nameof(TrayportTrade))
                {
                    logger.LogWarning("Unknown message type: {Type} — marking failed", msg.Type);
                    await inbox.MarkFailedAsync(msg, $"Unknown type: {msg.Type}", maxRetries: MaxRetries, ct);
                    failed++;
                    continue;
                }

                var raw = JsonSerializer.Deserialize<TrayportTrade>(msg.Payload)!;

                // 1. Map to internal canonical model
                var dto = TradeMapper.Map(raw);
                logger.LogInformation(
                    "Mapped trade {TradeId} → {InternalId} ({Direction} {Volume} {Unit} {Commodity})",
                    raw.TradeId, dto.InternalTradeId, dto.Direction, dto.Volume, dto.Unit, dto.Commodity);

                // 2. Set correlation context so outbox messages link back to this inbox message
                using (OutboxCorrelationContext.Set(msg.Id.ToString()))
                {
                    // 3. Fan-out via outbox — risk + settlement
                    await outbox.WriteAsync(
                        TradeMapper.ToRiskEvent(dto),
                        outboxName: Channel,
                        destination: "risk.trades",
                        ct: ct);

                    await outbox.WriteAsync(
                        TradeMapper.ToSettlement(dto),
                        outboxName: Channel,
                        destination: "settlement.requests",
                        ct: ct);
                }

                // 4. Mark inbox message done
                await inbox.MarkProcessedAsync(msg, ct);
                processed++;

                logger.LogInformation(
                    "Trade {Id} processed — 2 outbox messages queued (risk + settlement)",
                    dto.InternalTradeId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process trade message {Id}", msg.Id);
                await inbox.MarkFailedAsync(msg, ex.Message, maxRetries: MaxRetries, ct);
                failed++;
            }
        }

        logger.LogInformation(
            "ProcessTrades complete — processed: {Processed}, failed: {Failed}, total: {Total}",
            processed, failed, messages.Count);
    }
}
