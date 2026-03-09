namespace Relay.Sample.Trayport.Domain;

// ---------------------------------------------------------------------------
// Inbound — raw trade data from the Trayport trading system.
// ---------------------------------------------------------------------------

/// <summary>
/// Raw trade payload received from Trayport.
/// This is the external contract — never modify field names without versioning.
/// </summary>
public record TrayportTrade(
    string TradeId,
    string Commodity,
    string Counterparty,
    decimal Price,
    decimal Volume,
    string Unit,
    DateTime TradeDate,
    string Direction);   // "Buy" | "Sell"

// ---------------------------------------------------------------------------
// Internal — canonical trade representation used within our systems.
// ---------------------------------------------------------------------------

/// <summary>
/// Mapped internal trade model. Normalised counterparty, typed direction,
/// DateOnly for trade date.
/// </summary>
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

// ---------------------------------------------------------------------------
// Outbound — downstream events staged in the outbox.
// ---------------------------------------------------------------------------

/// <summary>
/// Sent to the risk system — notional = price × volume.
/// </summary>
public record RiskTradeEvent(
    string TradeId,
    string Commodity,
    decimal Notional,
    TradeDirection Direction);

/// <summary>
/// Sent to the settlement system — T+2 settlement date.
/// </summary>
public record SettlementRequest(
    string TradeId,
    string CounterpartyCode,
    decimal Amount,
    DateOnly SettleDate);
