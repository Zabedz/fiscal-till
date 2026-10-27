using System.Collections.Concurrent;
using Amazon.SimpleEmailV2.Model;
using Roller.Core;

namespace Roller.Journal.Tests;

internal sealed class InMemorySaleStore : ISaleStore
{
    private readonly ConcurrentDictionary<string, Sale> _sales = new();

    public Sale this[string saleId] => _sales[saleId];

    public void Add(Sale sale) => _sales[sale.SaleId] = sale;

    public Task<Sale?> PutIfAbsentAsync(Sale sale, CancellationToken ct) =>
        Task.FromResult(_sales.TryAdd(sale.SaleId, sale) ? null : _sales[sale.SaleId]);

    public Task<Sale?> GetAsync(string saleId, CancellationToken ct) => Task.FromResult(_sales.GetValueOrDefault(saleId));

    public Task<bool> MarkSignedAsync(Sale sale, Signature signature, CancellationToken ct) =>
        throw new NotSupportedException("Signing is not part of these tests");

    public Task<bool> MarkFailedAsync(Sale sale, string reason, CancellationToken ct) =>
        throw new NotSupportedException("Signing is not part of these tests");

    // Same conditions as the DynamoDB updates.
    public Task<bool> MarkJournaledAsync(string saleId, Receipt receipt, CancellationToken ct)
    {
        lock (_sales)
        {
            var sale = _sales[saleId];
            if (sale.Status != SaleStatus.Signed || (sale.Receipt is { } held && held.Number != receipt.Number))
            {
                return Task.FromResult(false);
            }
            _sales[saleId] = sale with { Receipt = receipt };
            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkEmailedAsync(string saleId, DateTimeOffset emailedAt, CancellationToken ct)
    {
        lock (_sales)
        {
            if (_sales[saleId].EmailedAt is not null)
            {
                return Task.FromResult(false);
            }
            _sales[saleId] = _sales[saleId] with { EmailedAt = emailedAt };
            return Task.FromResult(true);
        }
    }
}

/// <summary>Numbers sales per till in arrival order, keeping a sale's number on a repeat, like dbo.RecordSale.</summary>
internal sealed class InMemoryJournal : IJournalDb
{
    private readonly Dictionary<string, int> _numberBySale = [];
    private readonly Dictionary<string, int> _lastByTill = [];

    public Task<RecordedSale> RecordAsync(Sale sale, CancellationToken ct)
    {
        lock (_numberBySale)
        {
            if (_numberBySale.TryGetValue(sale.SaleId, out var existing))
            {
                return Task.FromResult(new RecordedSale(existing, true));
            }
            var till = $"{sale.VenueId}-{sale.TillId}";
            var next = _lastByTill.GetValueOrDefault(till) + 1;
            _lastByTill[till] = next;
            _numberBySale[sale.SaleId] = next;
            return Task.FromResult(new RecordedSale(next, false));
        }
    }
}

internal sealed class RecordingReceipts : IReceiptStore
{
    public Dictionary<string, string> Html { get; } = [];

    public Task PutAsync(string key, string html, string saleId, CancellationToken ct)
    {
        Html[key] = html;
        return Task.CompletedTask;
    }
}

internal sealed class RecordingMailer : IReceiptMailer
{
    public bool Fail { get; set; }

    public List<string> SentTo { get; } = [];

    public Task SendAsync(string to, string subject, string html, CancellationToken ct)
    {
        if (Fail)
        {
            throw new TooManyRequestsException("Maximum sending rate exceeded");
        }
        SentTo.Add(to);
        return Task.CompletedTask;
    }
}
