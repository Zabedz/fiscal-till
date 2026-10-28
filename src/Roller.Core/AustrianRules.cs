using System.Globalization;

namespace Roller.Core;

/// <summary>Austria (RKSV). Mock of the signed receipt data, without the turnover counter, certificate serial or chain.</summary>
[Market("AT")]
public sealed class AustrianRules(MarketConfig config) : MarketRules(config)
{
    private static readonly (string Field, decimal[] Rates)[] Fields =
    [
        ("Normal", [20m]),
        ("Ermaessigt-1", [10m]),
        ("Ermaessigt-2", [13m]),
        ("Null", [0m]),
        ("Besonders", [19m, 4.9m]),
    ];

    public override IReadOnlySet<decimal> FiscalRates { get; } = Fields.SelectMany(field => field.Rates).ToHashSet();

    public override string FiscalPayload(Sale sale, Venue venue)
    {
        RequireFiscalFields(sale);

        var soldAt = TimeZoneInfo.ConvertTime(sale.CreatedAt, venue.Zone);
        var amounts = Fields.Select(field => Amount(
            sale.Priced.VatByRate.Where(total => field.Rates.Contains(total.Rate)).Sum(total => total.GrossCents), ','));

        return string.Join('_', [
            "", "R1-AT0", RegisterId(sale), sale.SaleId, soldAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), .. amounts]);
    }
}
