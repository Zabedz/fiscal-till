namespace Roller.Core.Tests;

public class SaleValidatorTests
{
    private static readonly Venue Paris = VenueDirectory.Load().Get("paris-01");
    private static readonly IMarketRules French = MarketRulesRegistry.Load().For("FR");
    private static readonly DateOnly Today = new(2026, 10, 27);

    private static readonly SaleRequest Valid = new(
        "T01", [new SaleLineRequest("ADM-ADULT", "Adult admission", "admission", 2, 2500)], new Payment("card", 5000));

    [Fact]
    public void ValidSaleHasNoErrors()
    {
        Assert.Empty(SaleValidator.Validate(Valid, Paris, French, Today));
    }

    [Theory]
    [InlineData("T00")]
    [InlineData("T21")]
    [InlineData("T1")]
    [InlineData("t01")]
    [InlineData("T01\n")]
    [InlineData("T٠١")]
    public void TillOutsideTheVenueIsRejected(string tillId)
    {
        Assert.Contains(SaleValidator.Validate(Valid with { TillId = tillId }, Paris, French, Today), error => error.StartsWith("tillId"));
    }

    [Fact]
    public void NullLineIsRejected()
    {
        var withNull = Valid with { Lines = [Valid.Lines[0], null!] };

        Assert.Equal(new[] { "lines must hold 1 to 50 sale lines" }, SaleValidator.Validate(withNull, Paris, French, Today));
    }

    [Fact]
    public void TooManyLinesStopBeforeCheckingEachLine()
    {
        var tooMany = Valid with { Lines = Enumerable.Repeat(Valid.Lines[0] with { Category = "spa" }, 51).ToList() };

        Assert.Single(SaleValidator.Validate(tooMany, Paris, French, Today));
    }

    [Fact]
    public void PaymentMustMatchTheTotal()
    {
        var errors = SaleValidator.Validate(Valid with { Payment = new Payment("card", 4999) }, Paris, French, Today);

        Assert.Equal(new[] { "payment.amountCents 4999 does not match the sale total 5000" }, errors);
    }

    [Fact]
    public void CategoryWithoutARateIsRejected()
    {
        var spa = Valid with { Lines = [Valid.Lines[0] with { Category = "spa" }] };

        Assert.Contains(SaleValidator.Validate(spa, Paris, French, Today), error => error.Contains("category spa has no FR VAT rate"));
    }

    [Fact]
    public void LongValuesAreTruncatedInErrors()
    {
        var longCategory = Valid with { Lines = [Valid.Lines[0] with { Category = new string('x', 10_000) }] };

        Assert.All(SaleValidator.Validate(longCategory, Paris, French, Today), error => Assert.True(error.Length < 200));
    }

    [Fact]
    public void QuantityAndPaymentMethodAreChecked()
    {
        var bad = Valid with { Lines = [Valid.Lines[0] with { Quantity = 0 }], Payment = new Payment("voucher", 0) };

        var errors = SaleValidator.Validate(bad, Paris, French, Today);

        Assert.Contains(errors, error => error.StartsWith("lines[0].quantity"));
        Assert.Contains("payment.method must be cash or card", errors);
    }

    [Fact]
    public void MalformedEmailIsRejected()
    {
        Assert.Contains("customerEmail is not a valid address", SaleValidator.Validate(Valid with { CustomerEmail = "not an address" }, Paris, French, Today));
    }
}
