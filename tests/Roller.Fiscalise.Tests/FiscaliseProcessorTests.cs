using System.Text.Json;
using Amazon.Lambda.TestUtilities;
using Roller.Core;

namespace Roller.Fiscalise.Tests;

public class FiscaliseProcessorTests
{
    private const string SaleId = "4e413d67aebd4c3a76bdacc14e6037c9";

    private static readonly MarketRulesRegistry Markets = MarketRulesRegistry.Load();
    private static readonly VenueDirectory Venues = VenueDirectory.Load();
    private static readonly Signature PartnerSignature = new("paris-01-T01", 42, "c2lnbmVk", new DateTimeOffset(2026, 10, 27, 11, 30, 2, TimeSpan.Zero));

    private readonly InMemorySaleStore _sales = new();
    private readonly RecordingJournal _journal = new();
    private readonly RecordingVisibility _visibility = new();
    private readonly TestLambdaLogger _log = new();

    public FiscaliseProcessorTests() => _sales.Add(PendingSale());

    [Fact]
    public async Task PendingSaleIsSignedAndHandedToTheJournal()
    {
        var partner = new FakePartner((_, _) => new PartnerResult.Signed(PartnerSignature));

        Assert.True(await Processor(partner).ProcessAsync(Message(), _log, TestContext.Current.CancellationToken));

        Assert.Equal(SaleStatus.Signed, _sales[SaleId].Status);
        Assert.Equal(PartnerSignature, _sales[SaleId].Signature);
        Assert.Equal(new[] { SaleId }, _journal.SaleIds);
        Assert.Equal(Markets.For("FR").FiscalPayload(PendingSale(), Venues.Get("paris-01")), Assert.Single(partner.Calls).Payload);
    }

    [Fact]
    public async Task SignedSaleSkipsThePartnerAndIsHandedToTheJournalAgain()
    {
        _sales.Add(PendingSale() with { Status = SaleStatus.Signed, Signature = PartnerSignature });
        var partner = new FakePartner((_, _) => throw new InvalidOperationException("must not be called"));

        Assert.True(await Processor(partner).ProcessAsync(Message(), _log, TestContext.Current.CancellationToken));

        Assert.Empty(partner.Calls);
        Assert.Equal(new[] { SaleId }, _journal.SaleIds);
    }

    [Fact]
    public async Task FailedSaleIsDone()
    {
        _sales.Add(PendingSale() with { Status = SaleStatus.Failed });
        var partner = new FakePartner((_, _) => throw new InvalidOperationException("must not be called"));

        Assert.True(await Processor(partner).ProcessAsync(Message(), _log, TestContext.Current.CancellationToken));
        Assert.Empty(_journal.SaleIds);
    }

    [Fact]
    public async Task RejectedSaleIsMarkedFailedAndNotRetried()
    {
        var partner = new FakePartner((_, _) => new PartnerResult.Rejected("422 payload has no VAT key for 16%"));

        Assert.True(await Processor(partner).ProcessAsync(Message(), _log, TestContext.Current.CancellationToken));

        Assert.Equal(SaleStatus.Failed, _sales[SaleId].Status);
        Assert.Empty(_journal.SaleIds);
        Assert.Empty(_visibility.Delays);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(3, 20)]
    [InlineData(9, 60)]
    public async Task UnavailablePartnerRetriesLaterWithBackoff(int receiveCount, int baseSeconds)
    {
        var partner = new FakePartner((_, _) => new PartnerResult.Unavailable("503 Signing service unavailable"));

        Assert.False(await Processor(partner).ProcessAsync(Message(receiveCount: receiveCount), _log, TestContext.Current.CancellationToken));

        Assert.Equal(SaleStatus.Pending, _sales[SaleId].Status);
        var delay = Assert.Single(_visibility.Delays);
        Assert.InRange(delay.TotalSeconds, baseSeconds, baseSeconds * 1.2);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"saleId":"x"}""")]
    [InlineData("""{"saleId":"00000000000000000000000000000000","venueId":"paris-01","tillId":"T01"}""")]
    public async Task PoisonOrUnknownSaleIsReturnedAtOnceSoItReachesTheDlq(string body)
    {
        var partner = new FakePartner((_, _) => throw new InvalidOperationException("must not be called"));

        Assert.False(await Processor(partner).ProcessAsync(Message(body), _log, TestContext.Current.CancellationToken));

        Assert.Equal(TimeSpan.Zero, Assert.Single(_visibility.Delays));
    }

    [Fact]
    public async Task LosingTheRaceToAnotherInvocationStillCompletes()
    {
        // A duplicate delivery signs the sale while this one waits for the partner.
        var partner = new FakePartner((sale, _) =>
        {
            _sales.Add(sale with { Status = SaleStatus.Signed, Version = sale.Version + 1, Signature = PartnerSignature });
            return new PartnerResult.Signed(PartnerSignature);
        });

        Assert.True(await Processor(partner).ProcessAsync(Message(), _log, TestContext.Current.CancellationToken));
        Assert.Equal(new[] { SaleId }, _journal.SaleIds);
    }

    [Fact]
    public async Task UnexpectedErrorIsRetriedWithoutFailingTheBatch()
    {
        _sales.FailGet = _ => new TimeoutException("DynamoDB did not answer");
        var partner = new FakePartner((_, _) => new PartnerResult.Signed(PartnerSignature));

        Assert.False(await Processor(partner).ProcessAsync(Message(), _log, TestContext.Current.CancellationToken));
        Assert.Single(_visibility.Delays);
    }

    private FiscaliseProcessor Processor(IFiscalPartner partner) =>
        new(_sales, Venues, Markets, partner, _journal, _visibility, new Random(1));

    private static QueueMessage Message(string? body = null, int receiveCount = 1) => new(
        "m-1",
        "receipt-handle",
        body ?? JsonSerializer.Serialize(new SaleMessage(SaleId, "paris-01", "T01"), StrictJson.Options),
        receiveCount);

    private static Sale PendingSale() => new(
        SaleId: SaleId,
        VenueId: "paris-01",
        TillId: "T01",
        Market: "FR",
        Currency: "EUR",
        BusinessDate: new DateOnly(2026, 10, 27),
        CreatedAt: new DateTimeOffset(2026, 10, 27, 11, 30, 0, TimeSpan.Zero),
        Status: SaleStatus.Pending,
        Version: 1,
        IdempotencyKey: "k-1",
        RequestHash: "hash",
        Priced: Markets.For("FR").Price([new SaleLineRequest("ADM", "Admission", "admission", 2, 2500)], new DateOnly(2026, 10, 27)),
        Payment: new Payment("card", 5000),
        CustomerEmail: null);
}
