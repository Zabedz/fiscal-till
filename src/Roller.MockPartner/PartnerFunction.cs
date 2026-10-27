using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.DynamoDBv2;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SimpleSystemsManagement;

namespace Roller.MockPartner;

/// <summary>Mock fiscal signing service behind a Lambda function URL: PUT /signatures/{saleId}.</summary>
public sealed partial class PartnerFunction
{
    private const string KeyHeader = "x-partner-key";

    private readonly byte[] _apiKey;
    private readonly Switches _switches;
    private readonly SigningDevice _devices;

    public PartnerFunction()
    {
        var secrets = new AmazonSecretsManagerClient();
        _apiKey = Encoding.UTF8.GetBytes(ReadSecret(secrets, "API_KEY_SECRET_ARN"));
        _switches = new Switches(new AmazonSimpleSystemsManagementClient(), Required("SWITCHES_PARAMETER"), TimeProvider.System);
        _devices = new SigningDevice(
            new AmazonDynamoDBClient(),
            Required("PARTNER_TABLE"),
            Encoding.UTF8.GetBytes(ReadSecret(secrets, "SIGNING_KEY_SECRET_ARN")),
            TimeProvider.System);
    }

    public async Task<APIGatewayHttpApiV2ProxyResponse> Handle(APIGatewayHttpApiV2ProxyRequest request, ILambdaContext context)
    {
        var presented = request.Headers is { } headers && headers.TryGetValue(KeyHeader, out var value) ? value : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), _apiKey))
        {
            return Json(401, new { message = "Unknown partner key" });
        }

        var route = SignatureRoute().Match(request.RawPath ?? "");
        if (request.RequestContext?.Http?.Method != "PUT" || !route.Success)
        {
            return Json(404, new { message = "Only PUT /signatures/{saleId} is served" });
        }

        var switches = await _switches.CurrentAsync(CancellationToken.None);
        if (switches.Outage)
        {
            return Json(503, new { message = "Signing service unavailable" });
        }
        if (Random.Shared.NextDouble() < switches.ErrorRate)
        {
            return Json(500, new { message = "Injected signing error" });
        }

        SignRequest? body;
        try
        {
            // Function URLs base64-encode bodies whose content type is not text.
            var json = request.IsBase64Encoded ? Encoding.UTF8.GetString(Convert.FromBase64String(request.Body ?? "")) : request.Body ?? "";
            body = JsonSerializer.Deserialize<SignRequest>(json, PartnerJson.Options);
        }
        catch (Exception e) when (e is JsonException or FormatException)
        {
            return Json(400, new { message = $"Malformed signing request: {e.Message}" });
        }
        if (body is null || string.IsNullOrEmpty(body.Payload))
        {
            return Json(400, new { message = "Signing request needs a payload" });
        }

        var signed = await _devices.SignAsync(route.Groups[1].Value, body, CancellationToken.None);
        if (signed is null)
        {
            return Json(503, new { message = "Signing device busy" });
        }

        // Latency after signing models a slow answer to a completed signature; a retry gets the stored one.
        if (switches.LatencyMs > 0)
        {
            await Task.Delay(switches.LatencyMs);
        }
        context.Logger.LogInformation("Signed {SaleId} as {DeviceId} #{Counter}", signed.SaleId, signed.DeviceId, signed.Counter);
        return Json(200, signed);
    }

    private static APIGatewayHttpApiV2ProxyResponse Json(int statusCode, object body) => new()
    {
        StatusCode = statusCode,
        Body = JsonSerializer.Serialize(body, PartnerJson.Options),
        Headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
    };

    private static string ReadSecret(IAmazonSecretsManager secrets, string arnVariable) =>
        secrets.GetSecretValueAsync(new GetSecretValueRequest { SecretId = Required(arnVariable) }).GetAwaiter().GetResult().SecretString;

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"{name} is not set");

    [GeneratedRegex(@"^/signatures/([0-9a-f]{32})\z")]
    private static partial Regex SignatureRoute();
}
