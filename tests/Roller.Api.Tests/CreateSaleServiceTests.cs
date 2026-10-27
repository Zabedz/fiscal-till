using Microsoft.Extensions.Time.Testing;
using Roller.Core;

namespace Roller.Api.Tests;

public class CreateSaleServiceTests
{
    private const string Venue = "paris-01";

    private static readonly SaleRequest Request = new(
        "T01", [new SaleLineRequest("ADM-ADULT", "Adult admission", "admission", 2, 2500)], new Payment("card", 5000));

    private readonly InMemorySaleStore _store = new();
    private readonly RecordingQueue _queue = new();
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 27, 11, 30, 0, TimeSpan.Zero));
    private readonly CreateSaleService _service;

    public CreateSaleServiceTests()
    {
        _service = new CreateSaleService(VenueDirectory.Load(), MarketRulesRegistry.Load(), _store, _queue, _clock);
    }

    [Fact]
    public async Task NewSaleIsStoredPendingAndQueued()
    {
        var outcome = await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken);

        var sale = Assert.IsType<CreateSaleOutcome.Accepted>(outcome).Sale;
        Assert.Equal(SaleStatus.Pending, sale.Status);
        Assert.Equal(new DateOnly(2026, 10, 27), sale.BusinessDate);
        Assert.Equal(455, sale.Priced.VatCents);
        Assert.Equal(1, _store.Count);
        Assert.Equal(new[] { sale.SaleId }, _queue.SaleIds);
    }

    [Fact]
    public async Task RetryWithTheSameKeyAndBodyReturnsTheOriginalSale()
    {
        var first = await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken);
        var retry = await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken);

        var original = Assert.IsType<CreateSaleOutcome.Accepted>(first).Sale;
        Assert.Equal(original.SaleId, Assert.IsType<CreateSaleOutcome.Replayed>(retry).Sale.SaleId);
        Assert.Equal(1, _store.Count);
        // Still PENDING, so the retry queues it again in case the first send was lost.
        Assert.Equal(new[] { original.SaleId, original.SaleId }, _queue.SaleIds);
    }

    [Fact]
    public async Task RetryOfASignedSaleIsNotQueuedAgain()
    {
        var original = Assert.IsType<CreateSaleOutcome.Accepted>(
            await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken)).Sale;
        _store.Replace(original with { Status = SaleStatus.Signed });

        Assert.IsType<CreateSaleOutcome.Replayed>(await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken));
        Assert.Single(_queue.SaleIds);
    }

    [Fact]
    public async Task RetryAfterTheTradingDayEndsKeepsTheOriginalBusinessDate()
    {
        await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken);
        _clock.Advance(TimeSpan.FromDays(1));

        var retry = Assert.IsType<CreateSaleOutcome.Replayed>(
            await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken));
        Assert.Equal(new DateOnly(2026, 10, 27), retry.Sale.BusinessDate);
    }

    [Fact]
    public async Task SaleJustAfterMidnightGetsTheNewRateAndTheEveningsTradingDay()
    {
        // 00:30 in Berlin on 1 January 2026, when restaurant food dropped from 19% to 7%.
        var newYear = new FakeTimeProvider(new DateTimeOffset(2025, 12, 31, 23, 30, 0, TimeSpan.Zero));
        var service = new CreateSaleService(VenueDirectory.Load(), MarketRulesRegistry.Load(), _store, _queue, newYear);
        var meal = new SaleRequest("T01", [new SaleLineRequest("WURST", "Bratwurst", "food-onsite", 1, 450)], new Payment("card", 450));

        var sale = Assert.IsType<CreateSaleOutcome.Accepted>(
            await service.CreateAsync("berlin-01", "k-1", meal, TestContext.Current.CancellationToken)).Sale;

        Assert.Equal(7m, sale.Priced.Lines[0].VatRate);
        Assert.Equal(new DateOnly(2025, 12, 31), sale.BusinessDate);
    }

    [Fact]
    public async Task SameKeyWithADifferentBodyIsRejected()
    {
        await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken);
        var changed = Request with { Lines = [Request.Lines[0] with { Quantity = 3 }], Payment = new Payment("card", 7500) };

        Assert.IsType<CreateSaleOutcome.KeyReused>(await _service.CreateAsync(Venue, "k-1", changed, TestContext.Current.CancellationToken));
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public async Task SameBodyWithANewKeyIsANewSale()
    {
        await _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken);
        await _service.CreateAsync(Venue, "k-2", Request, TestContext.Current.CancellationToken);

        Assert.Equal(2, _store.Count);
    }

    [Fact]
    public async Task ConcurrentRetriesCreateOneSale()
    {
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            Task.Run(() => _service.CreateAsync(Venue, "k-1", Request, TestContext.Current.CancellationToken))));

        Assert.Single(outcomes.OfType<CreateSaleOutcome.Accepted>());
        Assert.Equal(19, outcomes.OfType<CreateSaleOutcome.Replayed>().Count());
        Assert.Equal(1, _store.Count);
    }

    [Fact]
    public async Task InvalidSaleIsNeitherStoredNorQueued()
    {
        var outcome = await _service.CreateAsync(Venue, "k-1", Request with { TillId = "T99" }, TestContext.Current.CancellationToken);

        Assert.IsType<CreateSaleOutcome.Invalid>(outcome);
        Assert.Equal(0, _store.Count);
        Assert.Empty(_queue.SaleIds);
    }
}
