using System.Globalization;

namespace Roller.Core;

/// <summary>Pricing shared by the markets: VAT extracted from each gross line and rounded half away from zero.</summary>
public abstract class MarketRules(MarketConfig config) : IMarketRules
{
    public string Market => config.Market;
    public string Currency => config.Currency;

    /// <summary>Rates the fiscal payload has a field for; null when it lists rates as they come.</summary>
    public virtual IReadOnlySet<decimal>? FiscalRates => null;

    public bool TryGetRate(string category, DateOnly supplyDate, out decimal rate) =>
        config.TryGetRate(category, supplyDate, out rate);

    public virtual PricedSale Price(IReadOnlyList<SaleLineRequest> lines, DateOnly supplyDate) =>
        new(lines.Select(line => PriceLine(line, supplyDate)).ToList());

    public abstract string FiscalPayload(Sale sale, Venue venue);

    protected static string Amount(long cents, char decimalSeparator) =>
        (cents / 100m).ToString("0.00", CultureInfo.InvariantCulture).Replace('.', decimalSeparator);

    /// <summary>Register id for the payload: till numbers repeat across venues, so the venue is part of it.</summary>
    protected static string RegisterId(Sale sale) => $"{sale.VenueId}-{sale.TillId}";

    protected void RequireFiscalFields(Sale sale)
    {
        var unknown = sale.Priced.VatByRate.Select(total => total.Rate).Where(rate => FiscalRates?.Contains(rate) == false).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"Sale {sale.SaleId} has {sale.Market} rates with no fiscal field: {string.Join(", ", unknown)}");
        }
    }

    private PricedLine PriceLine(SaleLineRequest line, DateOnly supplyDate)
    {
        if (!TryGetRate(line.Category, supplyDate, out var rate))
        {
            throw new ArgumentException($"No {Market} VAT rate for category {line.Category} on {supplyDate:yyyy-MM-dd}");
        }

        var grossCents = line.UnitPriceCents * line.Quantity;
        return new PricedLine(
            line.Sku, line.Description, line.Category, line.Quantity, line.UnitPriceCents, rate, grossCents, Vat.ExtractCents(grossCents, rate));
    }
}
