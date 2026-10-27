using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.Runtime;
using Roller.Core;

namespace Roller.Api;

public sealed class CreateSaleFunction
{
    // Stop AWS calls before Lambda's own timeout so the till gets a 503 it can retry.
    private static readonly TimeSpan TimeoutMargin = TimeSpan.FromSeconds(1);

    private static readonly HashSet<string> ThrottlingErrors =
        ["ThrottlingException", "ProvisionedThroughputExceededException", "RequestLimitExceeded"];

    private readonly VenueKeys _venueKeys;
    private readonly ApiResponses _responses;
    private readonly CreateSaleService _service;

    public CreateSaleFunction()
        : this(VenueKeys.FromEnvironment(), ApiResponses.FromEnvironment(), CreateSaleService.FromEnvironment())
    {
    }

    internal CreateSaleFunction(VenueKeys venueKeys, ApiResponses responses, CreateSaleService service)
    {
        _venueKeys = venueKeys;
        _responses = responses;
        _service = service;
    }

    public async Task<APIGatewayProxyResponse> Handle(APIGatewayProxyRequest request, ILambdaContext context)
    {
        if (!_venueKeys.TryGetVenue(request.RequestContext?.Identity?.ApiKeyId, out var venueId))
        {
            return _responses.UnknownVenue();
        }

        var key = RequestParts.Header(request, "Idempotency-Key");
        if (!RequestParts.IsValidIdempotencyKey(key))
        {
            return _responses.Of(400, new { message = "Idempotency-Key header must be 1 to 64 letters, digits, '-' or '_'" });
        }

        SaleRequest? sale;
        try
        {
            sale = JsonSerializer.Deserialize<SaleRequest>(request.Body ?? "", StrictJson.Options);
        }
        catch (JsonException e)
        {
            return _responses.Of(400, new { message = $"Malformed sale: {e.Message}" });
        }
        if (sale is null)
        {
            return _responses.Of(400, new { message = "Request body must be a sale object" });
        }

        var budget = context.RemainingTime - TimeoutMargin;
        using var timeout = new CancellationTokenSource(budget > TimeSpan.Zero ? budget : TimeSpan.Zero);
        CreateSaleOutcome outcome;
        try
        {
            outcome = await _service.CreateAsync(venueId, key, sale, timeout.Token);
        }
        catch (Exception e) when ((e is OperationCanceledException && timeout.IsCancellationRequested) || IsTransient(e))
        {
            context.Logger.LogWarning("Sale for key {IdempotencyKey} not confirmed: {Error}", key, e.Message);
            return _responses.Of(
                503,
                new { message = "Sale not confirmed; retry with the same Idempotency-Key" },
                new Dictionary<string, string> { ["Retry-After"] = "1" });
        }

        return outcome switch
        {
            CreateSaleOutcome.Accepted(var created) => _responses.Of(202, SaleView.From(created), Location(created)),
            CreateSaleOutcome.Replayed(var original) => _responses.Of(
                202, SaleView.From(original), new Dictionary<string, string>(Location(original)) { ["Idempotent-Replayed"] = "true" }),
            CreateSaleOutcome.KeyReused(var saleId) => _responses.Of(
                422, new { message = $"Idempotency-Key was already used for sale {saleId} with a different request" }),
            CreateSaleOutcome.Invalid(var errors) => _responses.Of(400, new { message = "Invalid sale", errors }),
            _ => throw new UnreachableException($"Unhandled outcome {outcome.GetType().Name}"),
        };
    }

    private static bool IsTransient(Exception e) =>
        e is AmazonServiceException service
        && (service.StatusCode >= HttpStatusCode.InternalServerError || ThrottlingErrors.Contains(service.ErrorCode));

    private static Dictionary<string, string> Location(Sale sale) => new() { ["Location"] = $"sales/{sale.SaleId}" };
}
