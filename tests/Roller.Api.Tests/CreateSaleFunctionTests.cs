using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Microsoft.Extensions.Time.Testing;
using Roller.Core;

namespace Roller.Api.Tests;

public class CreateSaleFunctionTests
{
    private const string ParisKeyId = "key-paris";

    private const string ValidBody =
        """{"tillId":"T01","lines":[{"sku":"ADM-ADULT","description":"Adult admission","category":"admission","quantity":2,"unitPriceCents":2500}],"payment":{"method":"card","amountCents":5000}}""";

    private readonly CreateSaleFunction _function = new(
        new VenueKeys(new Dictionary<string, string> { [ParisKeyId] = "paris-01" }),
        new ApiResponses("http://localhost:4200"),
        new CreateSaleService(
            VenueDirectory.Load(),
            MarketRulesRegistry.Load(),
            new InMemorySaleStore(),
            new RecordingQueue(),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 27, 11, 30, 0, TimeSpan.Zero))));

    [Fact]
    public async Task MissingIdempotencyKeyIsABadRequest()
    {
        var response = await _function.Handle(Request(ValidBody, idempotencyKey: null), Context());

        Assert.Equal(400, response.StatusCode);
        Assert.Contains("Idempotency-Key", response.Body);
    }

    [Fact]
    public async Task UnknownFieldIsABadRequest()
    {
        var body = ValidBody.Replace("\"tillId\":\"T01\"", "\"tillId\":\"T01\",\"discount\":10");

        Assert.Equal(400, (await _function.Handle(Request(body, "k-1"), Context())).StatusCode);
    }

    [Fact]
    public async Task AcceptedSaleReturns202WithItsLocation()
    {
        var response = await _function.Handle(Request(ValidBody, "k-1"), Context());

        Assert.Equal(202, response.StatusCode);
        Assert.Equal($"sales/{SaleIds.For("paris-01", "k-1")}", response.Headers["Location"]);
        Assert.Equal("http://localhost:4200", response.Headers["Access-Control-Allow-Origin"]);
        Assert.False(response.Headers.ContainsKey("Idempotent-Replayed"));
    }

    [Fact]
    public async Task RetryIsMarkedAsAReplay()
    {
        await _function.Handle(Request(ValidBody, "k-1"), Context());
        var retry = await _function.Handle(Request(ValidBody, "k-1"), Context());

        Assert.Equal(202, retry.StatusCode);
        Assert.Equal("true", retry.Headers["Idempotent-Replayed"]);
    }

    [Fact]
    public async Task ReusedKeyWithAnotherBodyIsUnprocessable()
    {
        await _function.Handle(Request(ValidBody, "k-1"), Context());
        var other = ValidBody.Replace("\"quantity\":2", "\"quantity\":1").Replace("\"amountCents\":5000", "\"amountCents\":2500");

        Assert.Equal(422, (await _function.Handle(Request(other, "k-1"), Context())).StatusCode);
    }

    [Fact]
    public async Task NullLineIsABadRequest()
    {
        var body = ValidBody.Replace("\"lines\":[", "\"lines\":[null,");

        Assert.Equal(400, (await _function.Handle(Request(body, "k-1"), Context())).StatusCode);
    }

    [Fact]
    public async Task RunningOutOfTimeIsARetryableServiceUnavailable()
    {
        var response = await _function.Handle(Request(ValidBody, "k-1"), new TestLambdaContext { RemainingTime = TimeSpan.FromMilliseconds(500) });

        Assert.Equal(503, response.StatusCode);
        Assert.Equal("1", response.Headers["Retry-After"]);
    }

    private static APIGatewayProxyRequest Request(string body, string? idempotencyKey) => new()
    {
        Body = body,
        Headers = idempotencyKey is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["idempotency-key"] = idempotencyKey },
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Identity = new APIGatewayProxyRequest.RequestIdentity { ApiKeyId = ParisKeyId },
        },
    };

    private static TestLambdaContext Context() => new() { RemainingTime = TimeSpan.FromSeconds(10) };
}
