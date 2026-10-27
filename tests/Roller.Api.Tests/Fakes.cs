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

    public void Replace(Sale sale) => _sales[sale.SaleId] = sale;
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
