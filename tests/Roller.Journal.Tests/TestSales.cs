using Roller.Core;

namespace Roller.Journal.Tests;

internal static class TestSales
{
    private static readonly MarketRulesRegistry Markets = MarketRulesRegistry.Load();

    public static Sale Signed(string saleId, string venueId = "paris-01", string tillId = "T01") => new(
        SaleId: saleId,
        VenueId: venueId,
        TillId: tillId,
        Market: "FR",
        Currency: "EUR",
        BusinessDate: new DateOnly(2026, 10, 27),
        CreatedAt: new DateTimeOffset(2026, 10, 27, 11, 30, 0, TimeSpan.Zero),
        Status: SaleStatus.Signed,
        Version: 2,
        IdempotencyKey: $"k-{saleId}",
        RequestHash: "hash",
        Priced: Markets.For("FR").Price(
            [new SaleLineRequest("ADM", "Entrée <adulte>", "admission", 2, 2500), new SaleLineRequest("VIN", "Vin rouge", "alcohol", 1, 600)],
            new DateOnly(2026, 10, 27)),
        Payment: new Payment("card", 5600),
        CustomerEmail: null,
        Signature: new Signature($"{venueId}-{tillId}", 1, "c2lnbmF0dXJl", new DateTimeOffset(2026, 10, 27, 11, 30, 2, TimeSpan.Zero)));
}
