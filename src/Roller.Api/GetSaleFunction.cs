using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

namespace Roller.Api;

public sealed class GetSaleFunction
{
    private readonly VenueKeys _venues = VenueKeys.FromEnvironment();
    private readonly ApiResponses _responses = ApiResponses.FromEnvironment();

    public APIGatewayProxyResponse Handle(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!_venues.TryGetVenue(request.RequestContext?.Identity?.ApiKeyId, out var venueId))
        {
            return _responses.UnknownVenue();
        }

        return _responses.Of(501, new { venueId, message = "Sale lookup is not implemented yet" });
    }
}
