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

/// <summary>The fiscal partner's signature. Counter is the signing device's transaction counter.</summary>
public sealed record Signature(string DeviceId, long Counter, string Value, DateTimeOffset SignedAt);

/// <summary>The journal's record of a signed sale: gapless number per till and the S3 key of the receipt document.</summary>
public sealed record Receipt(int Number, string Key, DateTimeOffset JournaledAt);

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
    string? CustomerEmail,
    Signature? Signature = null,
    Receipt? Receipt = null,
    DateTimeOffset? EmailedAt = null);

/// <summary>Body of fiscalise and journal queue messages; consumers read the sale itself from DynamoDB.</summary>
public sealed record SaleMessage(string SaleId, string VenueId, string TillId);
