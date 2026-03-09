using Microsoft.Extensions.Logging;
using Relay.Outbox.Core;

namespace Relay.Sample.Trayport.Domain;

/// <summary>
/// Simulates delivery to the risk management system.
/// In production: publish to Service Bus topic, RabbitMQ exchange, or HTTP call.
/// </summary>
public sealed class RiskPublisher(ILogger<RiskPublisher> logger)
    : IOutboxPublisher<RiskTradeEvent>
{
    public Task PublishAsync(
        RiskTradeEvent message,
        OutboxMessage envelope,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[risk] Trade {TradeId} {Direction} {Commodity} notional={Notional:N2} → {Destination}",
            message.TradeId, message.Direction, message.Commodity, message.Notional,
            envelope.Destination ?? "risk.trades");

        return Task.CompletedTask;
    }
}

/// <summary>
/// Simulates delivery to the settlement system.
/// In production: publish to Service Bus topic, RabbitMQ exchange, or HTTP call.
/// </summary>
public sealed class SettlementPublisher(ILogger<SettlementPublisher> logger)
    : IOutboxPublisher<SettlementRequest>
{
    public Task PublishAsync(
        SettlementRequest message,
        OutboxMessage envelope,
        CancellationToken ct = default)
    {
        logger.LogInformation(
            "[settlement] Trade {TradeId} cpty={Counterparty} amount={Amount:N2} settle={SettleDate} → {Destination}",
            message.TradeId, message.CounterpartyCode, message.Amount, message.SettleDate,
            envelope.Destination ?? "settlement.requests");

        return Task.CompletedTask;
    }
}
