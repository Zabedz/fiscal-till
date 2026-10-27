using Roller.Core;

namespace Roller.Journal.Tests;

public class ReceiptDocumentTests
{
    private static readonly string Html = ReceiptDocument.Render(
        TestSales.Signed("4e413d67aebd4c3a76bdacc14e6037c9"),
        VenueDirectory.Load().Get("paris-01"),
        MarketRulesRegistry.Load().For("FR"),
        42);

    [Fact]
    public void FormatsAmountsForTheMarketLocale()
    {
        // The space before the euro sign varies by ICU build (plain, no-break or narrow no-break).
        Assert.Matches(@"56,00\s€", Html);
        Assert.Contains("<html lang=\"fr\">", Html);
    }

    [Fact]
    public void ShowsTheReceiptNumberAndTheSignedPayload()
    {
        Assert.Contains("#00000042", Html);
        Assert.Contains("2000:600|1000:5000,5600,", Html);
    }

    [Fact]
    public void EncodesTextFromTheTill()
    {
        Assert.Contains("Entr&#233;e &lt;adulte&gt;", Html);
        Assert.DoesNotContain("<adulte>", Html);
    }
}
