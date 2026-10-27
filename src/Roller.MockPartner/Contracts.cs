using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace Roller.MockPartner;

/// <summary>Body of PUT /signatures/{saleId}. Payload is the market-specific data to sign.</summary>
public sealed record SignRequest(string VenueId, string TillId, string Market, string Payload);

public sealed record SignResponse(string SaleId, string DeviceId, long Counter, string Signature, DateTimeOffset SignedAt);

/// <summary>Drill switches, read from one SSM parameter.</summary>
public sealed record PartnerSwitches(int LatencyMs, double ErrorRate, bool Outage)
{
    public static readonly PartnerSwitches Healthy = new(0, 0, false);
}

internal static class PartnerJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };
}
