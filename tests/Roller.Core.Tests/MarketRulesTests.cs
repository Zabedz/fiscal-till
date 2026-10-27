namespace Roller.Core.Tests;

public class MarketRulesTests
{
    private static readonly MarketRulesRegistry Registry = MarketRulesRegistry.Load();
    private static readonly VenueDirectory Venues = VenueDirectory.Load();

    private static readonly Dictionary<string, string> VenueByMarket = new()
    {
        ["FR"] = "paris-01",
        ["DE"] = "berlin-01",
        ["AT"] = "vienna-01",
    };

    // 12:30 local in Paris, Berlin and Vienna (CET, after the October 2026 clock change).
    private static readonly DateTimeOffset SoldAt = new(2026, 10, 27, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public void PricesEachLineAndTotalsPerRateHighestFirst()
    {
        var priced = Registry.For("FR").Price(
            [Line("admission", 2, 2500), Line("alcohol", 1, 600)], new DateOnly(2026, 10, 27));

        Assert.Equal(5600, priced.GrossCents);
        Assert.Equal(555, priced.VatCents);
        Assert.Equal(5045, priced.NetCents);
        Assert.Equal(
            new[] { new VatTotal(20m, 600, 100), new VatTotal(10m, 5000, 455) },
            priced.VatByRate);
    }

    [Fact]
    public void FrenchPayloadListsGrossPerRateInBasisPoints()
    {
        var sale = NewSale("FR", new DateOnly(2026, 10, 27), new Payment("card", 5600), Line("admission", 2, 2500), Line("alcohol", 1, 600));

        Assert.Equal("2000:600|1000:5000,5600,20261027123000,paris-01-T01,sale-1", Payload(sale));
    }

    [Fact]
    public void GermanPayloadPutsGrossUnderTheGeneralAndReducedKeys()
    {
        var sale = NewSale("DE", new DateOnly(2026, 10, 27), new Payment("cash", 3900), Line("admission", 1, 3000), Line("food-onsite", 2, 450));

        Assert.Equal("Beleg^30.00_9.00_0.00_0.00_0.00^39.00:Bar", Payload(sale));
    }

    [Fact]
    public void GermanRestaurantFoodMovedFromTheGeneralToTheReducedKeyIn2026()
    {
        SaleLineRequest[] lines = [Line("admission", 1, 3000), Line("food-onsite", 2, 450)];

        Assert.Equal("Beleg^39.00_0.00_0.00_0.00_0.00^39.00:Unbar", Payload(NewSale("DE", new DateOnly(2025, 12, 31), new Payment("card", 3900), lines)));
        Assert.Equal("Beleg^30.00_9.00_0.00_0.00_0.00^39.00:Unbar", Payload(NewSale("DE", new DateOnly(2026, 1, 1), new Payment("card", 3900), lines)));
    }

    [Fact]
    public void AustrianPayloadFillsTheRksvAmountFields()
    {
        var sale = NewSale("AT", new DateOnly(2026, 10, 27), new Payment("card", 3050),
            Line("admission", 1, 1500), Line("soft-drink-onsite", 1, 400), Line("food-onsite", 1, 900), Line("staple-goods", 1, 250));

        Assert.Equal("_R1-AT0_vienna-01-T01_sale-1_2026-10-27T12:30:00_4,00_9,00_15,00_0,00_2,50", Payload(sale));
    }

    [Fact]
    public void GermanPayloadRefusesARateWithoutAKey()
    {
        var sixteen = new MarketConfig("DE", "EUR", new Dictionary<string, IReadOnlyList<RatePeriod>>
        {
            ["admission"] = [new(new DateOnly(2024, 1, 1), 16m)],
        });
        var rules = new GermanRules(sixteen);
        var sale = NewSale("DE", new DateOnly(2026, 10, 27), new Payment("card", 1000), Line("admission", 1, 1000)) with
        {
            Priced = rules.Price([Line("admission", 1, 1000)], new DateOnly(2026, 10, 27)),
        };

        Assert.Throws<InvalidOperationException>(() => rules.FiscalPayload(sale, Venues.Get("berlin-01")));
    }

    private static string Payload(Sale sale) => Registry.For(sale.Market).FiscalPayload(sale, Venues.Get(sale.VenueId));

    private static SaleLineRequest Line(string category, int quantity, long unitPriceCents) =>
        new($"SKU-{category}", category, category, quantity, unitPriceCents);

    private static Sale NewSale(string market, DateOnly supplyDate, Payment payment, params SaleLineRequest[] lines) => new(
        SaleId: "sale-1",
        VenueId: VenueByMarket[market],
        TillId: "T01",
        Market: market,
        Currency: "EUR",
        BusinessDate: supplyDate,
        CreatedAt: SoldAt,
        Status: SaleStatus.Pending,
        Version: 1,
        IdempotencyKey: "key",
        RequestHash: "hash",
        Priced: Registry.For(market).Price(lines, supplyDate),
        Payment: payment,
        CustomerEmail: null);
}
