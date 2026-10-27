using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Roller.Core;

namespace Roller.Api.Tests;

public class GetSaleFunctionTests
{
    private const string SaleId = "4e413d67aebd4c3a76bdacc14e6037c9";

    private readonly InMemorySaleStore _store = new();
    private readonly GetSaleFunction _function;

    public GetSaleFunctionTests()
    {
        _function = new GetSaleFunction(
            new VenueKeys(new Dictionary<string, string> { ["key-paris"] = "paris-01", ["key-berlin"] = "berlin-01" }),
            new ApiResponses("http://localhost:4200"),
            _store);
        _store.PutIfAbsentAsync(
            new Sale(SaleId, "paris-01", "T01", "FR", "EUR", new DateOnly(2026, 10, 27), DateTimeOffset.UnixEpoch, SaleStatus.Pending,
                1, "k-1", "hash", new PricedSale([]), new Payment("card", 0), null),
            CancellationToken.None);
    }

    [Fact]
    public async Task VenueReadsItsOwnSale()
    {
        var response = await _function.Handle(Request("key-paris", SaleId), new TestLambdaContext());

        Assert.Equal(200, response.StatusCode);
        Assert.Contains("\"status\":\"PENDING\"", response.Body);
    }

    [Fact]
    public async Task AnotherVenuesSaleIsNotFound()
    {
        Assert.Equal(404, (await _function.Handle(Request("key-berlin", SaleId), new TestLambdaContext())).StatusCode);
    }

    [Theory]
    [InlineData("00000000000000000000000000000000")]
    [InlineData("not-a-sale-id")]
    public async Task UnknownOrMalformedIdIsNotFound(string saleId)
    {
        Assert.Equal(404, (await _function.Handle(Request("key-paris", saleId), new TestLambdaContext())).StatusCode);
    }

    private static APIGatewayProxyRequest Request(string apiKeyId, string saleId) => new()
    {
        PathParameters = new Dictionary<string, string> { ["saleId"] = saleId },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Identity = new APIGatewayProxyRequest.RequestIdentity { ApiKeyId = apiKeyId },
        },
    };
}
