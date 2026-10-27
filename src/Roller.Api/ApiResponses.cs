using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Roller.Core;

namespace Roller.Api;

internal sealed class ApiResponses(string allowedOrigin)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static ApiResponses FromEnvironment() => new(Env.Required("ALLOWED_ORIGIN"));

    public APIGatewayProxyResponse Of(int statusCode, object body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var all = new Dictionary<string, string>
        {
            ["Content-Type"] = "application/json",
            ["Access-Control-Allow-Origin"] = allowedOrigin,
        };
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            all[name] = value;
        }

        return new APIGatewayProxyResponse { StatusCode = statusCode, Body = JsonSerializer.Serialize(body, Json), Headers = all };
    }

    public APIGatewayProxyResponse UnknownVenue() =>
        Of(403, new { message = "API key is not mapped to a venue" });
}
