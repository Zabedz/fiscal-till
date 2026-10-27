using Roller.Core;

namespace Roller.Api;

internal abstract record CreateSaleOutcome
{
    private CreateSaleOutcome() { }

    public sealed record Accepted(Sale Sale) : CreateSaleOutcome;
    public sealed record Replayed(Sale Sale) : CreateSaleOutcome;
    public sealed record KeyReused(string SaleId) : CreateSaleOutcome;
    public sealed record Invalid(IReadOnlyList<string> Errors) : CreateSaleOutcome;
}

internal sealed class CreateSaleService(
    VenueDirectory venues, MarketRulesRegistry markets, ISaleStore store, IFiscaliseQueue queue, TimeProvider clock)
{
    public static CreateSaleService FromEnvironment() => new(
        VenueDirectory.Load(), MarketRulesRegistry.Load(), DynamoSaleStore.FromEnvironment(), SqsFiscaliseQueue.FromEnvironment(), TimeProvider.System);

    public async Task<CreateSaleOutcome> CreateAsync(string venueId, string idempotencyKey, SaleRequest request, CancellationToken ct)
    {
        var venue = venues.Get(venueId);
        var rules = markets.For(venue.Market);
        var now = clock.GetUtcNow();
        var businessDate = BusinessDay.For(now, venue.Zone);
        var supplyDate = BusinessDay.SupplyDate(now, venue.Zone);

        var errors = SaleValidator.Validate(request, venue, rules, supplyDate);
        if (errors.Count > 0)
        {
            return new CreateSaleOutcome.Invalid(errors);
        }

        var sale = new Sale(
            SaleId: SaleIds.For(venueId, idempotencyKey),
            VenueId: venueId,
            TillId: request.TillId,
            Market: rules.Market,
            Currency: rules.Currency,
            BusinessDate: businessDate,
            CreatedAt: now,
            Status: SaleStatus.Pending,
            Version: 1,
            IdempotencyKey: idempotencyKey,
            RequestHash: SaleIds.RequestHash(request),
            Priced: rules.Price(request.Lines, supplyDate),
            Payment: request.Payment,
            CustomerEmail: request.CustomerEmail);

        var existing = await store.PutIfAbsentAsync(sale, ct);
        if (existing is null)
        {
            await queue.SendAsync(sale, ct);
            return new CreateSaleOutcome.Accepted(sale);
        }

        if (existing.RequestHash != sale.RequestHash)
        {
            return new CreateSaleOutcome.KeyReused(existing.SaleId);
        }

        // The first attempt may have stored the sale and then failed to queue it; the worker ignores duplicates.
        if (existing.Status == SaleStatus.Pending)
        {
            await queue.SendAsync(existing, ct);
        }
        return new CreateSaleOutcome.Replayed(existing);
    }
}
