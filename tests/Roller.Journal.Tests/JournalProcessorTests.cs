using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Roller.Core;

namespace Roller.Journal.Tests;

public class JournalProcessorTests
{
    private const string SaleId = "4e413d67aebd4c3a76bdacc14e6037c9";

    private readonly InMemorySaleStore _sales = new();
    private readonly InMemoryJournal _journal = new();
    private readonly RecordingReceipts _receipts = new();
    private readonly RecordingMailer _mailer = new();
    private readonly JournalProcessor _processor;

    public JournalProcessorTests()
    {
        _processor = new JournalProcessor(
            _sales, VenueDirectory.Load(), MarketRulesRegistry.Load(), _journal, _receipts, _mailer,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 11, 31, 0, TimeSpan.Zero)), NullLogger<JournalProcessor>.Instance);
    }

    [Fact]
    public async Task SignedSaleGetsAReceiptNumberAndADocument()
    {
        _sales.Add(TestSales.Signed(SaleId));

        Assert.Equal(JournalOutcome.Done, await _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken));

        var receipt = _sales[SaleId].Receipt!;
        Assert.Equal(1, receipt.Number);
        Assert.Equal("paris-01/2026-10-27/T01/00000001.html", receipt.Key);
        Assert.Contains("#00000001", _receipts.Html[receipt.Key]);
        Assert.Empty(_mailer.SentTo);
    }

    [Fact]
    public async Task RedeliveryKeepsTheReceiptNumber()
    {
        _sales.Add(TestSales.Signed(SaleId));
        await _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken);

        Assert.Equal(JournalOutcome.Done, await _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken));
        Assert.Equal(1, _sales[SaleId].Receipt!.Number);
    }

    [Fact]
    public async Task ReceiptIsEmailedOnceWhenTheSaleHasAnAddress()
    {
        _sales.Add(TestSales.Signed(SaleId) with { CustomerEmail = "visitor@example.com" });

        await _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken);
        await _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken);

        Assert.Equal(new[] { "visitor@example.com" }, _mailer.SentTo);
        Assert.NotNull(_sales[SaleId].EmailedAt);
    }

    [Fact]
    public async Task FailedEmailStillCompletesTheJournalEntry()
    {
        _sales.Add(TestSales.Signed(SaleId) with { CustomerEmail = "visitor@example.com" });
        _mailer.Fail = true;

        Assert.Equal(JournalOutcome.Done, await _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken));
        Assert.NotNull(_sales[SaleId].Receipt);
        Assert.Null(_sales[SaleId].EmailedAt);
    }

    [Fact]
    public async Task UnsignedSaleIsPoison()
    {
        _sales.Add(TestSales.Signed(SaleId) with { Status = SaleStatus.Pending, Signature = null });

        Assert.Equal(JournalOutcome.Poison, await _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken));
        Assert.Empty(_receipts.Html);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"saleId":"00000000000000000000000000000000","venueId":"paris-01","tillId":"T01"}""")]
    public async Task UnreadableOrUnknownMessageIsPoison(string body)
    {
        Assert.Equal(JournalOutcome.Poison, await _processor.ProcessAsync(body, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DifferentReceiptNumberOnTheSaleStopsTheMessage()
    {
        // A reset journal database would number this sale 1 again, while DynamoDB still holds its old number.
        _sales.Add(TestSales.Signed(SaleId) with { Receipt = new Receipt(7, "old-key", DateTimeOffset.UnixEpoch) });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _processor.ProcessAsync(Message(SaleId), TestContext.Current.CancellationToken));
    }

    private static string Message(string saleId) =>
        JsonSerializer.Serialize(new SaleMessage(saleId, "paris-01", "T01"), StrictJson.Options);
}
