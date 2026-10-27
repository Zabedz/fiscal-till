using Roller.Core;

namespace Roller.Api;

/// <summary>The sale as returned by POST /sales and GET /sales/{saleId}.</summary>
internal sealed record SaleView(
    string SaleId,
    string VenueId,
    string TillId,
    string Status,
    DateOnly BusinessDate,
    DateTimeOffset CreatedAt,
    string Currency,
    long GrossCents,
    long VatCents,
    long NetCents,
    IReadOnlyList<VatTotal> VatByRate,
    IReadOnlyList<PricedLine> Lines,
    Signature? Signature)
{
    public static SaleView From(Sale sale) => new(
        sale.SaleId,
        sale.VenueId,
        sale.TillId,
        sale.Status.ToString().ToUpperInvariant(),
        sale.BusinessDate,
        sale.CreatedAt,
        sale.Currency,
        sale.Priced.GrossCents,
        sale.Priced.VatCents,
        sale.Priced.NetCents,
        sale.Priced.VatByRate,
        sale.Priced.Lines,
        sale.Signature);
}
