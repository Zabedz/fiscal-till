using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;

namespace Roller.Api;

internal sealed class ApiResponses(string allowedOrigin)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ApiResponses FromEnvironment() =>
        new(Environment.GetEnvironmentVariable("ALLOWED_ORIGIN")
            ?? throw new InvalidOperationException("ALLOWED_ORIGIN is not set"));

    public APIGatewayProxyResponse Of(int statusCode, object body) => new()
    {
        StatusCode = statusCode,
        Body = JsonSerializer.Serialize(body, Json),
        Headers = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Access-Control-Allow-Origin"] = allowedOrigin,
        },
    };

    public APIGatewayProxyResponse UnknownVenue() =>
        Of(403, new { message = "API key is not mapped to a venue" });
}
