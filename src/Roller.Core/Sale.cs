namespace Roller.Core;

public sealed record SaleRequest(string TillId, IReadOnlyList<SaleLineRequest> Lines, Payment Payment, string? CustomerEmail = null);

/// <summary>A till line. UnitPriceCents is VAT-inclusive, as shelf prices are for consumers in the EU.</summary>
public sealed record SaleLineRequest(string Sku, string Description, string Category, int Quantity, long UnitPriceCents);

/// <summary>Method is "cash" or "card".</summary>
public sealed record Payment(string Method, long AmountCents);

public sealed record PricedLine(
    string Sku, string Description, string Category, int Quantity, long UnitPriceCents, decimal VatRate, long GrossCents, long VatCents)
{
    public long NetCents => GrossCents - VatCents;
}

public sealed record VatTotal(decimal Rate, long GrossCents, long VatCents)
{
    public long NetCents => GrossCents - VatCents;
}

public sealed record PricedSale(IReadOnlyList<PricedLine> Lines)
{
    public long GrossCents => Lines.Sum(line => line.GrossCents);
    public long VatCents => Lines.Sum(line => line.VatCents);
    public long NetCents => GrossCents - VatCents;

    /// <summary>Sums of the rounded line amounts per rate, highest rate first.</summary>
    public IReadOnlyList<VatTotal> VatByRate =>
        Lines.GroupBy(line => line.VatRate)
            .OrderByDescending(group => group.Key)
            .Select(group => new VatTotal(group.Key, group.Sum(line => line.GrossCents), group.Sum(line => line.VatCents)))
            .ToList();
}

public enum SaleStatus { Pending, Signed, Failed }

/// <summary>A sale as stored. BusinessDate is the venue's trading day; CreatedAt is UTC.</summary>
public sealed record Sale(
    string SaleId,
    string VenueId,
    string TillId,
    string Market,
    string Currency,
    DateOnly BusinessDate,
    DateTimeOffset CreatedAt,
    SaleStatus Status,
    int Version,
    string IdempotencyKey,
    string RequestHash,
    PricedSale Priced,
    Payment Payment,
    string? CustomerEmail);

/// <summary>Body of a fiscalise-queue message; the worker reads the sale itself from DynamoDB.</summary>
public sealed record FiscaliseMessage(string SaleId, string VenueId, string TillId);
