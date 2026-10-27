using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Roller.Core;

namespace Roller.Api;

internal interface IFiscaliseQueue
{
    Task SendAsync(Sale sale, CancellationToken ct);
}

internal sealed class SqsFiscaliseQueue(IAmazonSQS sqs, string queueUrl) : IFiscaliseQueue
{
    public static SqsFiscaliseQueue FromEnvironment() => new(new AmazonSQSClient(), Env.Required("FISCALISE_QUEUE_URL"));

    public Task SendAsync(Sale sale, CancellationToken ct) =>
        sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(new SaleMessage(sale.SaleId, sale.VenueId, sale.TillId), StrictJson.Options),
        }, ct);
}
