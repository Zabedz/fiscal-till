using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Roller.Core;

namespace Roller.Api;

public sealed class GetSaleFunction
{
    private readonly VenueKeys _venueKeys;
    private readonly ApiResponses _responses;
    private readonly ISaleStore _store;

    public GetSaleFunction()
        : this(VenueKeys.FromEnvironment(), ApiResponses.FromEnvironment(), DynamoSaleStore.FromEnvironment())
    {
    }

    internal GetSaleFunction(VenueKeys venueKeys, ApiResponses responses, ISaleStore store)
    {
        _venueKeys = venueKeys;
        _responses = responses;
        _store = store;
    }

    public async Task<APIGatewayProxyResponse> Handle(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!_venueKeys.TryGetVenue(request.RequestContext?.Identity?.ApiKeyId, out var venueId))
        {
            return _responses.UnknownVenue();
        }

        var saleId = request.PathParameters is { } path && path.TryGetValue("saleId", out var id) ? id : null;
        if (saleId is null || !SaleIds.IsWellFormed(saleId))
        {
            return NotFound(saleId);
        }

        var sale = await _store.GetAsync(saleId, CancellationToken.None);

        // Another venue's sale is reported as missing, so ids cannot be probed across venues.
        return sale is not null && sale.VenueId == venueId ? _responses.Of(200, SaleView.From(sale)) : NotFound(saleId);
    }

    private APIGatewayProxyResponse NotFound(string? saleId) => _responses.Of(404, new { message = $"Sale {saleId} not found" });
}
