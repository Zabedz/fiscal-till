namespace Roller.Core.Tests;

public class SaleItemTests
{
    [Fact]
    public void SaleSurvivesTheRoundTripThroughItsDynamoDbItem()
    {
        var sale = new Sale(
            SaleId: "4e413d67aebd4c3a76bdacc14e6037c9",
            VenueId: "paris-01",
            TillId: "T07",
            Market: "FR",
            Currency: "EUR",
            BusinessDate: new DateOnly(2026, 10, 27),
            CreatedAt: new DateTimeOffset(2026, 10, 27, 23, 30, 1, 123, TimeSpan.Zero),
            Status: SaleStatus.Signed,
            Version: 3,
            IdempotencyKey: "k-1",
            RequestHash: "abc123",
            Priced: new PricedSale([
                new PricedLine("BREAD", "Baguette", "staple-goods", 2, 120, 5.5m, 240, 13),
                new PricedLine("ADM", "Admission", "admission", 1, 2500, 10m, 2500, 227),
            ]),
            Payment: new Payment("cash", 2740),
            CustomerEmail: "visitor@example.com");

        var restored = SaleItem.ToSale(SaleItem.From(sale));

        Assert.Equivalent(sale, restored, strict: true);
        Assert.Equal("paris-01#2026-10-27", SaleItem.From(sale)["VenueDay"].S);
    }

    [Fact]
    public void SaleWithoutEmailHasNoEmailAttribute()
    {
        var sale = new Sale("id", "paris-01", "T01", "FR", "EUR", new DateOnly(2026, 10, 27), DateTimeOffset.UnixEpoch,
            SaleStatus.Pending, 1, "k", "h", new PricedSale([]), new Payment("card", 0), null);

        Assert.False(SaleItem.From(sale).ContainsKey("CustomerEmail"));
        Assert.Null(SaleItem.ToSale(SaleItem.From(sale)).CustomerEmail);
    }
}
