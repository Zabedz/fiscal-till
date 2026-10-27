using System.Text.Json;
using Amazon.Lambda.SQSEvents;
using Amazon.Lambda.TestUtilities;
using Roller.Core;

namespace Roller.Fiscalise.Tests;

public class FiscaliseFunctionTests
{
    private static readonly MarketRulesRegistry Markets = MarketRulesRegistry.Load();

    [Fact]
    public async Task RunningOutOfTimeFailsOnlyTheUnfinishedMessage()
    {
        var sales = new InMemorySaleStore();
        sales.Add(Sale("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        sales.Add(Sale("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"));
        sales.Hang.Add("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        var signature = new Signature("paris-01-T01", 1, "c2ln", DateTimeOffset.UnixEpoch);
        var journal = new RecordingJournal();
        var function = new FiscaliseFunction(new FiscaliseProcessor(
            sales, VenueDirectory.Load(), Markets, new FakePartner((_, _) => new PartnerResult.Signed(signature)),
            journal, new RecordingVisibility(), new Random(1)));

        var response = await function.Handle(
            new SQSEvent { Records = [Record("m-a", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Record("m-b", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")] },
            new TestLambdaContext { RemainingTime = TimeSpan.FromSeconds(1.5) });

        Assert.Equal("m-b", Assert.Single(response.BatchItemFailures).ItemIdentifier);
        Assert.Equal(new[] { "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" }, journal.SaleIds);
    }

    private static SQSEvent.SQSMessage Record(string messageId, string saleId) => new()
    {
        MessageId = messageId,
        ReceiptHandle = $"handle-{messageId}",
        Body = JsonSerializer.Serialize(new SaleMessage(saleId, "paris-01", "T01"), StrictJson.Options),
        Attributes = new Dictionary<string, string> { ["ApproximateReceiveCount"] = "1" },
    };

    private static Sale Sale(string saleId) => new(
        saleId, "paris-01", "T01", "FR", "EUR", new DateOnly(2026, 10, 27), new DateTimeOffset(2026, 10, 27, 11, 30, 0, TimeSpan.Zero),
        SaleStatus.Pending, 1, "k", "h",
        Markets.For("FR").Price([new SaleLineRequest("ADM", "Admission", "admission", 1, 2500)], new DateOnly(2026, 10, 27)),
        new Payment("card", 2500), null);
}
