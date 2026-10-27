namespace Roller.Core;

/// <summary>
/// Tax rules for one market. A market whose tills take exact payment needs one class marked [Market("XX")]
/// and one entry in config/markets.json.
/// </summary>
public interface IMarketRules
{
    string Market { get; }
    string Currency { get; }

    /// <summary>Culture name (fr-FR) for amounts and dates shown to customers.</summary>
    string Locale { get; }

    bool TryGetRate(string category, DateOnly supplyDate, out decimal rate);
    PricedSale Price(IReadOnlyList<SaleLineRequest> lines, DateOnly supplyDate);

    /// <summary>The sale's totals in the layout this market's fiscal regime signs.</summary>
    string FiscalPayload(Sale sale, Venue venue);
}

/// <summary>Names the market code in config/markets.json that an <see cref="IMarketRules"/> class implements.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class MarketAttribute(string code) : Attribute
{
    public string Code { get; } = code;
}
