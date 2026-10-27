namespace Roller.Core;

public static class Vat
{
    /// <summary>VAT contained in a VAT-inclusive amount, rounded half away from zero to a whole cent.</summary>
    public static long ExtractCents(long grossCents, decimal ratePercent) =>
        // Explicit mode: Math.Round defaults to banker's rounding (ToEven), which turns 2.5 cents into 2.
        (long)Math.Round(grossCents * ratePercent / (100m + ratePercent), MidpointRounding.AwayFromZero);
}
