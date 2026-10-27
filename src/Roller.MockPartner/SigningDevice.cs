using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Roller.MockPartner;

/// <summary>
/// One signing device per till, modelled loosely on a TSE: each signature takes the device's next counter value.
/// Signing is idempotent per sale, and the counter has no gaps.
/// </summary>
internal sealed class SigningDevice(IAmazonDynamoDB dynamo, string tableName, byte[] signingKey, TimeProvider clock)
{
    private const int MaxAttempts = 10;
    private const int PauseStepMs = 5;
    private const int PauseSpreadMs = 20;

    /// <summary>Returns the sale's signature, creating it on first call; null when the device stayed contended.</summary>
    public async Task<SignResponse?> SignAsync(string saleId, SignRequest request, CancellationToken ct)
    {
        var deviceId = $"{request.VenueId}-{request.TillId}";
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            if (await ExistingAsync(saleId, ct) is { } existing)
            {
                return existing;
            }

            var counter = await CounterAsync(deviceId, ct);
            var signed = new SignResponse(saleId, deviceId, counter + 1, "", clock.GetUtcNow());
            signed = signed with { Signature = Sign(signed, request.Payload) };

            try
            {
                await dynamo.TransactWriteItemsAsync(new TransactWriteItemsRequest
                {
                    TransactItems =
                    [
                        new TransactWriteItem { Update = AdvanceCounter(deviceId, counter) },
                        new TransactWriteItem { Put = StoreSignature(signed, request.Payload) },
                    ],
                }, ct);
                return signed;
            }
            catch (TransactionCanceledException)
            {
                // Another sale took this counter value, or a duplicate request signed this sale: pause, then read again.
                await Task.Delay(TimeSpan.FromMilliseconds(Random.Shared.Next(PauseStepMs, PauseSpreadMs) * attempt), ct);
            }
        }
        return null;
    }

    private string Sign(SignResponse signed, string payload)
    {
        var data = $"{signed.DeviceId}|{signed.Counter}|{Timestamp(signed.SignedAt)}|{signed.SaleId}|{payload}";
        return Convert.ToBase64String(HMACSHA256.HashData(signingKey, Encoding.UTF8.GetBytes(data)));
    }

    private async Task<SignResponse?> ExistingAsync(string saleId, CancellationToken ct)
    {
        var item = (await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Key($"SIG#{saleId}"),
            ConsistentRead = true,
        }, ct)).Item;

        return item is { Count: > 0 }
            ? new SignResponse(
                saleId,
                item["DeviceId"].S,
                long.Parse(item["Counter"].N, CultureInfo.InvariantCulture),
                item["Signature"].S,
                DateTimeOffset.Parse(item["SignedAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind))
            : null;
    }

    private async Task<long> CounterAsync(string deviceId, CancellationToken ct)
    {
        var item = (await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = Key($"DEVICE#{deviceId}"),
            ConsistentRead = true,
        }, ct)).Item;

        return item is { Count: > 0 } ? long.Parse(item["Counter"].N, CultureInfo.InvariantCulture) : 0;
    }

    private Update AdvanceCounter(string deviceId, long current) => new()
    {
        TableName = tableName,
        Key = Key($"DEVICE#{deviceId}"),
        UpdateExpression = "SET #counter = :next",
        ConditionExpression = "attribute_not_exists(Pk) OR #counter = :current",
        ExpressionAttributeNames = new Dictionary<string, string> { ["#counter"] = "Counter" },
        ExpressionAttributeValues = new Dictionary<string, AttributeValue>
        {
            [":current"] = Number(current),
            [":next"] = Number(current + 1),
        },
    };

    private Put StoreSignature(SignResponse signed, string payload) => new()
    {
        TableName = tableName,
        Item = new Dictionary<string, AttributeValue>
        {
            ["Pk"] = new() { S = $"SIG#{signed.SaleId}" },
            ["DeviceId"] = new() { S = signed.DeviceId },
            ["Counter"] = Number(signed.Counter),
            ["Signature"] = new() { S = signed.Signature },
            ["SignedAt"] = new() { S = Timestamp(signed.SignedAt) },
            ["Payload"] = new() { S = payload },
        },
        ConditionExpression = "attribute_not_exists(Pk)",
    };

    private static Dictionary<string, AttributeValue> Key(string pk) => new() { ["Pk"] = new() { S = pk } };

    private static AttributeValue Number(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };

    private static string Timestamp(DateTimeOffset instant) => instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
