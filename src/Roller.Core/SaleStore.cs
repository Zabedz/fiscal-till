using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Roller.Core;

public interface ISaleStore
{
    /// <summary>Stores the sale unless its id is taken; returns the sale already stored under that id, or null.</summary>
    Task<Sale?> PutIfAbsentAsync(Sale sale, CancellationToken ct);

    Task<Sale?> GetAsync(string saleId, CancellationToken ct);

    /// <summary>PENDING to SIGNED, only if the sale is unchanged since it was read; false when another writer got there first.</summary>
    Task<bool> MarkSignedAsync(Sale sale, Signature signature, CancellationToken ct);

    /// <summary>PENDING to FAILED, for a sale the partner rejected; false when the sale had already moved on.</summary>
    Task<bool> MarkFailedAsync(Sale sale, string reason, CancellationToken ct);

    /// <summary>Records the journal receipt on a SIGNED sale; false when it already holds a different receipt number.</summary>
    Task<bool> MarkJournaledAsync(string saleId, Receipt receipt, CancellationToken ct);

    /// <summary>Sets EmailedAt after the receipt email went out; false when it was already set.</summary>
    Task<bool> MarkEmailedAsync(string saleId, DateTimeOffset emailedAt, CancellationToken ct);
}

public sealed class DynamoSaleStore(IAmazonDynamoDB dynamo, string tableName) : ISaleStore
{
    public static DynamoSaleStore FromEnvironment() => new(new AmazonDynamoDBClient(), Env.Required("SALES_TABLE"));

    public async Task<Sale?> PutIfAbsentAsync(Sale sale, CancellationToken ct)
    {
        try
        {
            await dynamo.PutItemAsync(new PutItemRequest
            {
                TableName = tableName,
                Item = SaleItem.From(sale),
                ConditionExpression = "attribute_not_exists(SaleId)",
                ReturnValuesOnConditionCheckFailure = ReturnValuesOnConditionCheckFailure.ALL_OLD,
            }, ct);
            return null;
        }
        catch (ConditionalCheckFailedException e) when (e.Item is { Count: > 0 })
        {
            return SaleItem.ToSale(e.Item);
        }
    }

    public async Task<Sale?> GetAsync(string saleId, CancellationToken ct)
    {
        var response = await dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = tableName,
            Key = new Dictionary<string, AttributeValue> { ["SaleId"] = new() { S = saleId } },
            ConsistentRead = true,
        }, ct);

        return response.Item is { Count: > 0 } item ? SaleItem.ToSale(item) : null;
    }

    public Task<bool> MarkSignedAsync(Sale sale, Signature signature, CancellationToken ct) =>
        TransitionAsync(sale, SaleStatus.Signed, "SignatureDeviceId = :device, SignatureCounter = :counter, SignatureValue = :value, SignedAt = :signedAt",
            new()
            {
                [":device"] = new() { S = signature.DeviceId },
                [":counter"] = new() { N = signature.Counter.ToString(CultureInfo.InvariantCulture) },
                [":value"] = new() { S = signature.Value },
                [":signedAt"] = new() { S = SaleItem.Timestamp(signature.SignedAt) },
            }, ct);

    public Task<bool> MarkFailedAsync(Sale sale, string reason, CancellationToken ct) =>
        TransitionAsync(sale, SaleStatus.Failed, "FailureReason = :reason", new() { [":reason"] = new() { S = reason } }, ct);

    public Task<bool> MarkJournaledAsync(string saleId, Receipt receipt, CancellationToken ct) =>
        ConditionalUpdateAsync(saleId,
            "SET ReceiptNumber = :number, ReceiptKey = :key, JournaledAt = :at",
            "#status = :signed AND (attribute_not_exists(ReceiptNumber) OR ReceiptNumber = :number)",
            new()
            {
                [":number"] = new() { N = receipt.Number.ToString(CultureInfo.InvariantCulture) },
                [":key"] = new() { S = receipt.Key },
                [":at"] = new() { S = SaleItem.Timestamp(receipt.JournaledAt) },
                [":signed"] = new() { S = SaleItem.StatusName(SaleStatus.Signed) },
            }, ct);

    public Task<bool> MarkEmailedAsync(string saleId, DateTimeOffset emailedAt, CancellationToken ct) =>
        ConditionalUpdateAsync(saleId, "SET EmailedAt = :at", "attribute_not_exists(EmailedAt)",
            new() { [":at"] = new() { S = SaleItem.Timestamp(emailedAt) } }, ct);

    private async Task<bool> ConditionalUpdateAsync(
        string saleId, string update, string condition, Dictionary<string, AttributeValue> values, CancellationToken ct)
    {
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = tableName,
                Key = new Dictionary<string, AttributeValue> { ["SaleId"] = new() { S = saleId } },
                UpdateExpression = update,
                ConditionExpression = condition,
                ExpressionAttributeNames = condition.Contains("#status") ? new Dictionary<string, string> { ["#status"] = "Status" } : null,
                ExpressionAttributeValues = values,
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }

    private async Task<bool> TransitionAsync(
        Sale sale, SaleStatus to, string setFields, Dictionary<string, AttributeValue> values, CancellationToken ct)
    {
        values[":to"] = new() { S = SaleItem.StatusName(to) };
        values[":pending"] = new() { S = SaleItem.StatusName(SaleStatus.Pending) };
        values[":version"] = new() { N = sale.Version.ToString(CultureInfo.InvariantCulture) };
        values[":next"] = new() { N = (sale.Version + 1).ToString(CultureInfo.InvariantCulture) };
        try
        {
            await dynamo.UpdateItemAsync(new UpdateItemRequest
            {
                TableName = tableName,
                Key = new Dictionary<string, AttributeValue> { ["SaleId"] = new() { S = sale.SaleId } },
                UpdateExpression = $"SET #status = :to, Version = :next, {setFields}",
                ConditionExpression = "#status = :pending AND Version = :version",
                ExpressionAttributeNames = new Dictionary<string, string> { ["#status"] = "Status" },
                ExpressionAttributeValues = values,
            }, ct);
            return true;
        }
        catch (ConditionalCheckFailedException)
        {
            return false;
        }
    }
}

/// <summary>Maps a sale to its DynamoDB item. VenueDay and CreatedAt are the ByVenueDay index keys.</summary>
internal static class SaleItem
{
    public static Dictionary<string, AttributeValue> From(Sale sale)
    {
        var businessDate = sale.BusinessDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var item = new Dictionary<string, AttributeValue>
        {
            ["SaleId"] = S(sale.SaleId),
            ["VenueId"] = S(sale.VenueId),
            ["TillId"] = S(sale.TillId),
            ["Market"] = S(sale.Market),
            ["Currency"] = S(sale.Currency),
            ["BusinessDate"] = S(businessDate),
            ["VenueDay"] = S($"{sale.VenueId}#{businessDate}"),
            ["CreatedAt"] = S(Timestamp(sale.CreatedAt)),
            ["Status"] = S(StatusName(sale.Status)),
            ["Version"] = N(sale.Version),
            ["IdempotencyKey"] = S(sale.IdempotencyKey),
            ["RequestHash"] = S(sale.RequestHash),
            ["Lines"] = new() { L = sale.Priced.Lines.Select(LineItem).ToList() },
            ["GrossCents"] = N(sale.Priced.GrossCents),
            ["VatCents"] = N(sale.Priced.VatCents),
            ["PaymentMethod"] = S(sale.Payment.Method),
            ["PaymentCents"] = N(sale.Payment.AmountCents),
        };
        if (sale.CustomerEmail is not null)
        {
            item["CustomerEmail"] = S(sale.CustomerEmail);
        }
        if (sale.Signature is { } signature)
        {
            item["SignatureDeviceId"] = S(signature.DeviceId);
            item["SignatureCounter"] = N(signature.Counter);
            item["SignatureValue"] = S(signature.Value);
            item["SignedAt"] = S(Timestamp(signature.SignedAt));
        }
        if (sale.Receipt is { } receipt)
        {
            item["ReceiptNumber"] = N(receipt.Number);
            item["ReceiptKey"] = S(receipt.Key);
            item["JournaledAt"] = S(Timestamp(receipt.JournaledAt));
        }
        if (sale.EmailedAt is { } emailedAt)
        {
            item["EmailedAt"] = S(Timestamp(emailedAt));
        }
        return item;
    }

    public static string Timestamp(DateTimeOffset instant) => instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    public static string StatusName(SaleStatus status) => status.ToString().ToUpperInvariant();

    public static Sale ToSale(Dictionary<string, AttributeValue> item) => new(
        SaleId: item["SaleId"].S,
        VenueId: item["VenueId"].S,
        TillId: item["TillId"].S,
        Market: item["Market"].S,
        Currency: item["Currency"].S,
        BusinessDate: DateOnly.ParseExact(item["BusinessDate"].S, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        CreatedAt: ParseTimestamp(item["CreatedAt"].S),
        Status: Enum.Parse<SaleStatus>(item["Status"].S, ignoreCase: true),
        Version: int.Parse(item["Version"].N, CultureInfo.InvariantCulture),
        IdempotencyKey: item["IdempotencyKey"].S,
        RequestHash: item["RequestHash"].S,
        Priced: new PricedSale(item["Lines"].L.Select(entry => ToLine(entry.M)).ToList()),
        Payment: new Payment(item["PaymentMethod"].S, long.Parse(item["PaymentCents"].N, CultureInfo.InvariantCulture)),
        CustomerEmail: item.TryGetValue("CustomerEmail", out var email) ? email.S : null,
        Signature: item.ContainsKey("SignatureValue")
            ? new Signature(
                item["SignatureDeviceId"].S,
                long.Parse(item["SignatureCounter"].N, CultureInfo.InvariantCulture),
                item["SignatureValue"].S,
                ParseTimestamp(item["SignedAt"].S))
            : null,
        Receipt: item.ContainsKey("ReceiptNumber")
            ? new Receipt(
                int.Parse(item["ReceiptNumber"].N, CultureInfo.InvariantCulture),
                item["ReceiptKey"].S,
                ParseTimestamp(item["JournaledAt"].S))
            : null,
        EmailedAt: item.TryGetValue("EmailedAt", out var emailed) ? ParseTimestamp(emailed.S) : null);

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static AttributeValue LineItem(PricedLine line) => new()
    {
        M = new Dictionary<string, AttributeValue>
        {
            ["Sku"] = S(line.Sku),
            ["Description"] = S(line.Description),
            ["Category"] = S(line.Category),
            ["Quantity"] = N(line.Quantity),
            ["UnitPriceCents"] = N(line.UnitPriceCents),
            ["VatRate"] = new() { N = line.VatRate.ToString(CultureInfo.InvariantCulture) },
            ["GrossCents"] = N(line.GrossCents),
            ["VatCents"] = N(line.VatCents),
        },
    };

    private static PricedLine ToLine(Dictionary<string, AttributeValue> line) => new(
        line["Sku"].S,
        line["Description"].S,
        line["Category"].S,
        int.Parse(line["Quantity"].N, CultureInfo.InvariantCulture),
        long.Parse(line["UnitPriceCents"].N, CultureInfo.InvariantCulture),
        decimal.Parse(line["VatRate"].N, CultureInfo.InvariantCulture),
        long.Parse(line["GrossCents"].N, CultureInfo.InvariantCulture),
        long.Parse(line["VatCents"].N, CultureInfo.InvariantCulture));

    private static AttributeValue S(string value) => new() { S = value };

    private static AttributeValue N(long value) => new() { N = value.ToString(CultureInfo.InvariantCulture) };
}
