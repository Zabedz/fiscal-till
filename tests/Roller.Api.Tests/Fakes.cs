using System.Collections.Concurrent;
using Roller.Core;

namespace Roller.Api.Tests;

/// <summary>Same contract as the DynamoDB conditional put: the first writer of an id wins.</summary>
internal sealed class InMemorySaleStore : ISaleStore
{
    private readonly ConcurrentDictionary<string, Sale> _sales = new();

    public int Count => _sales.Count;

    public Task<Sale?> PutIfAbsentAsync(Sale sale, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_sales.TryAdd(sale.SaleId, sale) ? null : _sales[sale.SaleId]);
    }

    public Task<Sale?> GetAsync(string saleId, CancellationToken ct) =>
        Task.FromResult(_sales.GetValueOrDefault(saleId));

    public Task<bool> MarkSignedAsync(Sale sale, Signature signature, CancellationToken ct) =>
        Task.FromResult(Transition(sale, current => current with { Status = SaleStatus.Signed, Version = current.Version + 1, Signature = signature }));

    public Task<bool> MarkFailedAsync(Sale sale, string reason, CancellationToken ct) =>
        Task.FromResult(Transition(sale, current => current with { Status = SaleStatus.Failed, Version = current.Version + 1 }));

    public void Replace(Sale sale) => _sales[sale.SaleId] = sale;

    public Task<bool> MarkJournaledAsync(string saleId, Receipt receipt, CancellationToken ct) =>
        throw new NotSupportedException("The journal is not part of these tests");

    public Task<bool> MarkEmailedAsync(string saleId, DateTimeOffset emailedAt, CancellationToken ct) =>
        throw new NotSupportedException("The journal is not part of these tests");

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

internal sealed class RecordingQueue : IFiscaliseQueue
{
    private readonly ConcurrentQueue<string> _saleIds = new();

    public IReadOnlyList<string> SaleIds => _saleIds.ToList();

    public Task SendAsync(Sale sale, CancellationToken ct)
    {
        _saleIds.Enqueue(sale.SaleId);
        return Task.CompletedTask;
    }
}
