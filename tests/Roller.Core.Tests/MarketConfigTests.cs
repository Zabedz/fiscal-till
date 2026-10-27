using System.Globalization;

namespace Roller.Core.Tests;

public class MarketConfigTests
{
    private static readonly IReadOnlyDictionary<string, MarketConfig> Markets =
        MarketConfig.Load().ToDictionary(config => config.Market);

    [Theory]
    [InlineData("DE", "food-onsite", "2025-12-31", "19")]
    [InlineData("DE", "food-onsite", "2026-01-01", "7")]
    [InlineData("AT", "staple-goods", "2026-06-30", "10")]
    [InlineData("AT", "staple-goods", "2026-07-01", "4.9")]
    [InlineData("AT", "food-takeaway", "2026-07-01", "10")]
    [InlineData("FR", "admission", "2026-10-27", "10")]
    [InlineData("FR", "staple-goods", "2026-10-27", "5.5")]
    public void UsesTheRateInForceOnTheSupplyDate(string market, string category, string date, string expectedRate)
    {
        Assert.True(Markets[market].TryGetRate(category, DateOnly.Parse(date, CultureInfo.InvariantCulture), out var rate));
        Assert.Equal(decimal.Parse(expectedRate, CultureInfo.InvariantCulture), rate);
    }

    [Fact]
    public void UnknownCategoryHasNoRate()
    {
        Assert.False(Markets["FR"].TryGetRate("spa", new DateOnly(2026, 10, 27), out _));
    }

    [Fact]
    public void DateBeforeTheFirstPeriodHasNoRate()
    {
        Assert.False(Markets["DE"].TryGetRate("admission", new DateOnly(2023, 12, 31), out _));
    }

    [Fact]
    public void ShippedConfigHasNoProblems()
    {
        Assert.Empty(Markets.Values.SelectMany(config => config.Problems()));
    }

    [Fact]
    public void TwoRatesFromTheSameDateAreAProblem()
    {
        var config = new MarketConfig("AT", "EUR", new Dictionary<string, IReadOnlyList<RatePeriod>>
        {
            ["staple-goods"] = [new(new DateOnly(2026, 7, 1), 4.9m), new(new DateOnly(2026, 7, 1), 5.9m)],
        });

        Assert.Equal(new[] { "AT/staple-goods: two rates from 2026-07-01" }, config.Problems());
    }
}
