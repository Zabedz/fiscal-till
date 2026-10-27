using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;

namespace Roller.Core;

public interface ISaleStore
{
    /// <summary>Stores the sale unless its id is taken; returns the sale already stored under that id, or null.</summary>
    Task<Sale?> PutIfAbsentAsync(Sale sale, CancellationToken ct);

    Task<Sale?> GetAsync(string saleId, CancellationToken ct);
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
            ["CreatedAt"] = S(sale.CreatedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)),
            ["Status"] = S(sale.Status.ToString().ToUpperInvariant()),
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
        return item;
    }

    public static Sale ToSale(Dictionary<string, AttributeValue> item) => new(
        SaleId: item["SaleId"].S,
        VenueId: item["VenueId"].S,
        TillId: item["TillId"].S,
        Market: item["Market"].S,
        Currency: item["Currency"].S,
        BusinessDate: DateOnly.ParseExact(item["BusinessDate"].S, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        CreatedAt: DateTimeOffset.Parse(item["CreatedAt"].S, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Status: Enum.Parse<SaleStatus>(item["Status"].S, ignoreCase: true),
        Version: int.Parse(item["Version"].N, CultureInfo.InvariantCulture),
        IdempotencyKey: item["IdempotencyKey"].S,
        RequestHash: item["RequestHash"].S,
        Priced: new PricedSale(item["Lines"].L.Select(entry => ToLine(entry.M)).ToList()),
        Payment: new Payment(item["PaymentMethod"].S, long.Parse(item["PaymentCents"].N, CultureInfo.InvariantCulture)),
        CustomerEmail: item.TryGetValue("CustomerEmail", out var email) ? email.S : null);

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
