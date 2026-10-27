using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;

namespace Roller.Api;

public sealed class CreateSaleFunction
{
    private readonly VenueKeys _venues = VenueKeys.FromEnvironment();
    private readonly ApiResponses _responses = ApiResponses.FromEnvironment();

    public APIGatewayProxyResponse Handle(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!_venues.TryGetVenue(request.RequestContext?.Identity?.ApiKeyId, out var venueId))
        {
            return _responses.UnknownVenue();
        }

        return _responses.Of(202, new { venueId, stub = true });
    }
}
