using System.Globalization;

namespace Roller.Core;

/// <summary>France (NF525). Mock of the per-rate totals a certified till signs and chains.</summary>
[Market("FR")]
public sealed class FrenchRules(MarketConfig config) : MarketRules(config)
{
    private const int BasisPointsPerPercent = 100;

    public override string FiscalPayload(Sale sale, Venue venue)
    {
        var perRate = sale.Priced.VatByRate.Select(total => $"{(int)(total.Rate * BasisPointsPerPercent)}:{total.GrossCents}");
        var soldAt = TimeZoneInfo.ConvertTime(sale.CreatedAt, venue.Zone);

        return string.Join(',', [
            string.Join('|', perRate),
            sale.Priced.GrossCents.ToString(CultureInfo.InvariantCulture),
            soldAt.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            RegisterId(sale),
            sale.SaleId]);
    }
}
