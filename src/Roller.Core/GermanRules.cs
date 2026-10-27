namespace Roller.Core;

/// <summary>Germany (KassenSichV). Mock of the TSE processData "Kassenbeleg-V1" layout.</summary>
[Market("DE")]
public sealed class GermanRules(MarketConfig config) : MarketRules(config)
{
    private const decimal GeneralRate = 19m;
    private const decimal ReducedRate = 7m;
    private const decimal ZeroRate = 0m;

    public override IReadOnlySet<decimal> FiscalRates { get; } = new HashSet<decimal> { GeneralRate, ReducedRate, ZeroRate };

    // Gross per DSFinV-K VAT key (general, reduced, two agricultural averages, zero), then the payment.
    public override string FiscalPayload(Sale sale, Venue venue)
    {
        RequireFiscalFields(sale);

        string GrossAt(decimal rate) =>
            Amount(sale.Priced.VatByRate.Where(total => total.Rate == rate).Sum(total => total.GrossCents), '.');

        var paymentType = sale.Payment.Method == "cash" ? "Bar" : "Unbar";
        return $"Beleg^{GrossAt(GeneralRate)}_{GrossAt(ReducedRate)}_0.00_0.00_{GrossAt(ZeroRate)}"
            + $"^{Amount(sale.Payment.AmountCents, '.')}:{paymentType}";
    }
}
