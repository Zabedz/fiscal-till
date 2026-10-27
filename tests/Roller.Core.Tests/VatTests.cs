using System.Globalization;

namespace Roller.Core.Tests;

public class VatTests
{
    [Theory]
    [InlineData(1000, "10", 91)]
    [InlineData(1000, "20", 167)]
    [InlineData(1000, "5.5", 52)]
    [InlineData(1190, "19", 190)]
    [InlineData(1070, "7", 70)]
    [InlineData(1049, "4.9", 49)]
    [InlineData(3, "20", 1)]
    public void ExtractsVatFromAGrossAmount(long grossCents, string rate, long expectedVatCents)
    {
        Assert.Equal(expectedVatCents, Vat.ExtractCents(grossCents, decimal.Parse(rate, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void MidpointRoundsAwayFromZeroWhereMathRoundsDefaultRoundsToEven()
    {
        // 15 cents at 20% holds exactly 2.5 cents of VAT.
        Assert.Equal(2m, Math.Round(2.5m));
        Assert.Equal(3, Vat.ExtractCents(15, 20m));
    }
}
