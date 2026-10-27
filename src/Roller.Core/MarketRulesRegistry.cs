using System.Reflection;

namespace Roller.Core;

public sealed class MarketRulesRegistry
{
    private readonly Dictionary<string, IMarketRules> _byMarket;

    private MarketRulesRegistry(Dictionary<string, IMarketRules> byMarket) => _byMarket = byMarket;

    public IReadOnlyCollection<string> Markets => _byMarket.Keys;

    public static MarketRulesRegistry Load() => From(MarketConfig.Load());

    /// <summary>
    /// Pairs each config entry with the class marked [Market] for its code. Fails on a missing side, an unusable
    /// rate table, or a configured rate the market's fiscal payload has no field for.
    /// </summary>
    public static MarketRulesRegistry From(IReadOnlyList<MarketConfig> configs)
    {
        var rulesTypes = typeof(IMarketRules).Assembly.GetTypes()
            .Select(type => (Type: type, Code: type.GetCustomAttribute<MarketAttribute>()?.Code))
            .Where(entry => entry.Code is not null)
            .ToDictionary(entry => entry.Code!, entry => entry.Type);

        var configured = configs.Select(config => config.Market).ToHashSet();
        var withoutClass = configured.Except(rulesTypes.Keys).ToList();
        var withoutConfig = rulesTypes.Keys.Except(configured).ToList();
        if (withoutClass.Count > 0 || withoutConfig.Count > 0)
        {
            throw new InvalidOperationException(
                $"Markets without a rules class: [{string.Join(", ", withoutClass)}]; " +
                $"rules classes without a config entry: [{string.Join(", ", withoutConfig)}]");
        }

        var byMarket = configs.ToDictionary(
            config => config.Market,
            config => (IMarketRules)Activator.CreateInstance(rulesTypes[config.Market], config)!);

        var problems = configs.SelectMany(config => config.Problems())
            .Concat(configs.SelectMany(config => UnsignableRates(config, byMarket[config.Market])))
            .ToList();
        if (problems.Count > 0)
        {
            throw new InvalidOperationException($"Invalid market config: {string.Join("; ", problems)}");
        }

        return new MarketRulesRegistry(byMarket);
    }

    public IMarketRules For(string market) =>
        _byMarket.TryGetValue(market, out var rules) ? rules : throw new KeyNotFoundException($"No rules for market {market}");

    private static IEnumerable<string> UnsignableRates(MarketConfig config, IMarketRules rules) =>
        rules is MarketRules { FiscalRates: { } fiscalRates }
            ? config.AllRates.Where(rate => !fiscalRates.Contains(rate)).Select(rate => $"{config.Market}: rate {rate} has no fiscal field")
            : [];
}
