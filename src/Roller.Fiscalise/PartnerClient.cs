using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Roller.Core;

namespace Roller.Fiscalise;

internal abstract record PartnerResult
{
    private PartnerResult() { }

    public sealed record Signed(Signature Signature) : PartnerResult;

    /// <summary>The partner judged the sale's data unsignable (422).</summary>
    public sealed record Rejected(string Reason) : PartnerResult;

    /// <summary>Timeout, outage, throttling or an auth fault on our side; retry later.</summary>
    public sealed record Unavailable(string Reason) : PartnerResult;
}

internal interface IFiscalPartner
{
    Task<PartnerResult> SignAsync(Sale sale, string payload, CancellationToken ct);
}

internal sealed class PartnerClient(HttpClient http, string apiKey) : IFiscalPartner
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public async Task<PartnerResult> SignAsync(Sale sale, string payload, CancellationToken ct)
    {
        using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
        call.CancelAfter(CallTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Put, $"signatures/{sale.SaleId}")
        {
            Content = JsonContent.Create(new SignRequest(sale.VenueId, sale.TillId, sale.Market, payload), options: Json),
        };
        request.Headers.Add("x-partner-key", apiKey);

        try
        {
            using var response = await http.SendAsync(request, call.Token);
            if (response.IsSuccessStatusCode)
            {
                var signed = await response.Content.ReadFromJsonAsync<SignResponse>(Json, call.Token);
                return signed is { DeviceId.Length: > 0, Signature.Length: > 0, Counter: > 0 } && signed.SaleId == sale.SaleId
                    ? new PartnerResult.Signed(new Signature(signed.DeviceId, signed.Counter, signed.Signature, signed.SignedAt))
                    : new PartnerResult.Unavailable($"Signing response does not describe sale {sale.SaleId}");
            }

            var reason = $"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync(call.Token)}";
            // Only 422 judges the sale. 400, 401 and 403 point at our request or config, so sales wait as PENDING and alarm via the DLQ.
            return response.StatusCode == HttpStatusCode.UnprocessableEntity
                ? new PartnerResult.Rejected(reason)
                : new PartnerResult.Unavailable(reason);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return new PartnerResult.Unavailable(e is OperationCanceledException ? $"No answer within {CallTimeout.TotalSeconds:0} s" : e.Message);
        }
    }

    private sealed record SignRequest(string VenueId, string TillId, string Market, string Payload);

    private sealed record SignResponse(string SaleId, string DeviceId, long Counter, string Signature, DateTimeOffset SignedAt);
}
