namespace Roller.Core;

/// <summary>A VAT rate in percent, applying to supplies from a local calendar date until the next period starts.</summary>
public sealed record RatePeriod(DateOnly From, decimal Rate);

/// <summary>One market from config/markets.json: currency and dated VAT rates per sale category.</summary>
public sealed record MarketConfig(string Market, string Currency, IReadOnlyDictionary<string, IReadOnlyList<RatePeriod>> Categories)
{
    private const decimal MaxRate = 100m;

    public static IReadOnlyList<MarketConfig> Load() => StrictJson.ReadConfig<List<MarketConfig>>("markets.json");

    public IEnumerable<decimal> AllRates => Categories.Values.SelectMany(periods => periods).Select(period => period.Rate).Distinct();

    public bool TryGetRate(string category, DateOnly supplyDate, out decimal rate)
    {
        var current = Categories.TryGetValue(category, out var periods)
            ? periods.Where(period => period.From <= supplyDate).MaxBy(period => period.From)
            : null;

        rate = current?.Rate ?? 0;
        return current is not null;
    }

    /// <summary>Problems that would make rate lookups ambiguous or meaningless; empty when the entry is usable.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (Currency.Length != 3)
        {
            problems.Add($"{Market}: currency {Currency} is not a three-letter code");
        }

        foreach (var (category, periods) in Categories)
        {
            if (periods.Count == 0)
            {
                problems.Add($"{Market}/{category}: no rate periods");
            }
            foreach (var duplicate in periods.GroupBy(period => period.From).Where(group => group.Count() > 1))
            {
                problems.Add($"{Market}/{category}: two rates from {duplicate.Key:yyyy-MM-dd}");
            }
            foreach (var period in periods.Where(period => period.Rate is < 0 or >= MaxRate))
            {
                problems.Add($"{Market}/{category}: rate {period.Rate} from {period.From:yyyy-MM-dd} is outside 0 to {MaxRate}");
            }
        }
        return problems;
    }
}
