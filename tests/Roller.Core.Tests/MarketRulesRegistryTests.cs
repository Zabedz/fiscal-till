namespace Roller.Core.Tests;

public class MarketRulesRegistryTests
{
    [Fact]
    public void EveryConfiguredMarketHasARulesClass()
    {
        var registry = MarketRulesRegistry.Load();

        Assert.Equal(new[] { "AT", "DE", "FR" }, registry.Markets.Order());
        Assert.IsType<AustrianRules>(registry.For("AT"));
        Assert.IsType<GermanRules>(registry.For("DE"));
        Assert.IsType<FrenchRules>(registry.For("FR"));
    }

    [Fact]
    public void ConfigEntryWithoutARulesClassFailsToLoad()
    {
        var configs = MarketConfig.Load()
            .Append(new MarketConfig("CH", "CHF", "de-CH", new Dictionary<string, IReadOnlyList<RatePeriod>>()))
            .ToList();

        var error = Assert.Throws<InvalidOperationException>(() => MarketRulesRegistry.From(configs));
        Assert.Contains("without a rules class: [CH]", error.Message);
    }

    [Fact]
    public void RulesClassWithoutAConfigEntryFailsToLoad()
    {
        var configs = MarketConfig.Load().Where(config => config.Market != "AT").ToList();

        var error = Assert.Throws<InvalidOperationException>(() => MarketRulesRegistry.From(configs));
        Assert.Contains("without a config entry: [AT]", error.Message);
    }

    [Fact]
    public void RateWithoutAFiscalFieldFailsToLoad()
    {
        // Germany's second half of 2020 used 16%, which this mock's TSE layout has no key for.
        var configs = MarketConfig.Load()
            .Select(config => config.Market != "DE" ? config : config with
            {
                Categories = new Dictionary<string, IReadOnlyList<RatePeriod>>(config.Categories)
                {
                    ["admission"] = [new(new DateOnly(2024, 1, 1), 16m)],
                },
            })
            .ToList();

        var error = Assert.Throws<InvalidOperationException>(() => MarketRulesRegistry.From(configs));
        Assert.Contains("DE: rate 16 has no fiscal field", error.Message);
    }
}
