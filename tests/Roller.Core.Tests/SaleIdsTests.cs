using System.Text.Json;

namespace Roller.Core.Tests;

public class SaleIdsTests
{
    private static readonly SaleRequest Request = new(
        "T01", [new SaleLineRequest("ADM-ADULT", "Adult admission", "admission", 2, 2500)], new Payment("card", 5000));

    [Fact]
    public void SaleIdIsTheFirst32HexOfSha256OfVenueNewlineKey()
    {
        // printf 'paris-01\nk-1' | shasum -a 256 | cut -c1-32
        Assert.Equal("4e413d67aebd4c3a76bdacc14e6037c9", SaleIds.For("paris-01", "k-1"));
    }

    [Fact]
    public void SameKeyAtAnotherVenueIsAnotherSale()
    {
        Assert.NotEqual(SaleIds.For("paris-01", "k-1"), SaleIds.For("berlin-01", "k-1"));
    }

    [Theory]
    [InlineData("4e413d67aebd4c3a76bdacc14e6037c9", true)]
    [InlineData("4E413D67AEBD4C3A76BDACC14E6037C9", false)]
    [InlineData("4e413d67aebd4c3a76bdacc14e6037c", false)]
    [InlineData("4e413d67aebd4c3a76bdacc14e6037c9\n", false)]
    public void RecognisesWellFormedSaleIds(string saleId, bool expected)
    {
        Assert.Equal(expected, SaleIds.IsWellFormed(saleId));
    }

    [Fact]
    public void RequestHashIgnoresJsonFormattingAndFieldOrder()
    {
        const string compact =
            """{"tillId":"T01","lines":[{"sku":"ADM-ADULT","description":"Adult admission","category":"admission","quantity":2,"unitPriceCents":2500}],"payment":{"method":"card","amountCents":5000}}""";
        const string reordered = """
            {
              "payment": { "amountCents": 5000, "method": "card" },
              "lines": [ { "quantity": 2, "unitPriceCents": 2500, "category": "admission", "description": "Adult admission", "sku": "ADM-ADULT" } ],
              "tillId": "T01"
            }
            """;

        var first = JsonSerializer.Deserialize<SaleRequest>(compact, StrictJson.Options)!;
        var second = JsonSerializer.Deserialize<SaleRequest>(reordered, StrictJson.Options)!;

        Assert.Equal(SaleIds.RequestHash(first), SaleIds.RequestHash(second));
    }

    [Fact]
    public void RequestHashChangesWithTheContent()
    {
        var changed = Request with { Lines = [Request.Lines[0] with { Quantity = 3 }] };

        Assert.NotEqual(SaleIds.RequestHash(Request), SaleIds.RequestHash(changed));
    }
}
