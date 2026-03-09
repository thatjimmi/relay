namespace Relay.Sample.Trayport.Domain;

/// <summary>
/// Maps between the external Trayport trade format and internal canonical models.
/// Pure functions — no side-effects, easily unit-testable.
/// </summary>
public static class TradeMapper
{
    /// <summary>Map raw Trayport trade to internal canonical model.</summary>
    public static TradeDto Map(TrayportTrade t) => new(
        InternalTradeId: $"TPT-{t.TradeId}",
        Commodity: t.Commodity,
        CounterpartyCode: NormaliseCounterparty(t.Counterparty),
        Price: t.Price,
        Volume: t.Volume,
        Unit: t.Unit,
        TradeDate: DateOnly.FromDateTime(t.TradeDate),
        Direction: Enum.Parse<TradeDirection>(t.Direction, ignoreCase: true));

    /// <summary>Create a risk event from the canonical trade.</summary>
    public static RiskTradeEvent ToRiskEvent(TradeDto dto) => new(
        dto.InternalTradeId,
        dto.Commodity,
        dto.Price * dto.Volume,
        dto.Direction);

    /// <summary>Create a settlement request from the canonical trade (T+2).</summary>
    public static SettlementRequest ToSettlement(TradeDto dto) => new(
        dto.InternalTradeId,
        dto.CounterpartyCode,
        dto.Price * dto.Volume,
        dto.TradeDate.AddDays(2));

    /// <summary>Normalise counterparty name to uppercase, spaces → underscores.</summary>
    public static string NormaliseCounterparty(string name) =>
        name.Trim().ToUpperInvariant().Replace(" ", "_");
}
