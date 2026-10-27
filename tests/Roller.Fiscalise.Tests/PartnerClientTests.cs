using System.Net;
using Roller.Core;

namespace Roller.Fiscalise.Tests;

public class PartnerClientTests
{
    private const string SaleId = "4e413d67aebd4c3a76bdacc14e6037c9";

    private static readonly Sale Sale = new(
        SaleId, "paris-01", "T01", "FR", "EUR", new DateOnly(2026, 10, 27), DateTimeOffset.UnixEpoch, SaleStatus.Pending, 1, "k", "h",
        new PricedSale([]), new Payment("card", 0), null);

    [Fact]
    public async Task SignedResponseForThisSaleIsASignature()
    {
        var result = await SignWith(HttpStatusCode.OK,
            $$"""{"saleId":"{{SaleId}}","deviceId":"paris-01-T01","counter":7,"signature":"c2ln","signedAt":"2026-10-27T11:30:02Z"}""");

        Assert.Equal(7, Assert.IsType<PartnerResult.Signed>(result).Signature.Counter);
    }

    [Theory]
    [InlineData("""{"saleId":"00000000000000000000000000000000","deviceId":"paris-01-T01","counter":7,"signature":"c2ln","signedAt":"2026-10-27T11:30:02Z"}""")]
    [InlineData("""{"saleId":"4e413d67aebd4c3a76bdacc14e6037c9","deviceId":"paris-01-T01","counter":0,"signature":"","signedAt":"2026-10-27T11:30:02Z"}""")]
    [InlineData("{}")]
    public async Task SuccessThatDoesNotDescribeTheSaleIsRetried(string body)
    {
        Assert.IsType<PartnerResult.Unavailable>(await SignWith(HttpStatusCode.OK, body));
    }

    [Fact]
    public async Task UnprocessableSaleIsRejected()
    {
        Assert.IsType<PartnerResult.Rejected>(await SignWith(HttpStatusCode.UnprocessableEntity, """{"message":"no VAT key for 16%"}"""));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task OtherFailuresAreRetried(HttpStatusCode status)
    {
        Assert.IsType<PartnerResult.Unavailable>(await SignWith(status, """{"message":"failed"}"""));
    }

    private static Task<PartnerResult> SignWith(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://partner.test/") };
        return new PartnerClient(http, "key").SignAsync(Sale, "payload", TestContext.Current.CancellationToken);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
