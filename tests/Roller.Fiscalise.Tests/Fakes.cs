using System.Collections.Concurrent;
using Roller.Core;

namespace Roller.Fiscalise.Tests;

internal sealed class InMemorySaleStore : ISaleStore
{
    private readonly ConcurrentDictionary<string, Sale> _sales = new();

    public Func<string, Exception?> FailGet { get; set; } = _ => null;

    public Sale this[string saleId] => _sales[saleId];

    public void Add(Sale sale) => _sales[sale.SaleId] = sale;

    public Task<Sale?> PutIfAbsentAsync(Sale sale, CancellationToken ct) =>
        Task.FromResult(_sales.TryAdd(sale.SaleId, sale) ? null : _sales[sale.SaleId]);

    /// <summary>Sales whose read never completes until the caller's token is cancelled.</summary>
    public HashSet<string> Hang { get; } = [];

    public async Task<Sale?> GetAsync(string saleId, CancellationToken ct)
    {
        if (Hang.Contains(saleId))
        {
            await Task.Delay(Timeout.Infinite, ct);
        }
        return FailGet(saleId) is { } error ? throw error : _sales.GetValueOrDefault(saleId);
    }

    public Task<bool> MarkSignedAsync(Sale sale, Signature signature, CancellationToken ct) =>
        Task.FromResult(Transition(sale, current => current with { Status = SaleStatus.Signed, Version = current.Version + 1, Signature = signature }));

    public Task<bool> MarkFailedAsync(Sale sale, string reason, CancellationToken ct) =>
        Task.FromResult(Transition(sale, current => current with { Status = SaleStatus.Failed, Version = current.Version + 1 }));

    // Same condition as the DynamoDB update: still PENDING and at the version the caller read.
    private bool Transition(Sale read, Func<Sale, Sale> change)
    {
        lock (_sales)
        {
            if (!_sales.TryGetValue(read.SaleId, out var current) || current.Status != SaleStatus.Pending || current.Version != read.Version)
            {
                return false;
            }
            _sales[read.SaleId] = change(current);
            return true;
        }
    }
}

internal sealed class FakePartner(Func<Sale, string, PartnerResult> answer) : IFiscalPartner
{
    public List<(string SaleId, string Payload)> Calls { get; } = [];

    public Task<PartnerResult> SignAsync(Sale sale, string payload, CancellationToken ct)
    {
        Calls.Add((sale.SaleId, payload));
        return Task.FromResult(answer(sale, payload));
    }
}

internal sealed class RecordingJournal : IJournalQueue
{
    public List<string> SaleIds { get; } = [];

    public Task SendAsync(Sale sale, CancellationToken ct)
    {
        SaleIds.Add(sale.SaleId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingVisibility : IMessageVisibility
{
    public List<TimeSpan> Delays { get; } = [];

    public Task DelayAsync(string receiptHandle, TimeSpan delay, CancellationToken ct)
    {
        Delays.Add(delay);
        return Task.CompletedTask;
    }
}
