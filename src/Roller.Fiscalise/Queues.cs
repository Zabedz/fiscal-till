using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Roller.Core;

namespace Roller.Fiscalise;

internal interface IJournalQueue
{
    Task SendAsync(Sale sale, CancellationToken ct);
}

/// <summary>FIFO, one message group per till, deduplicated on the sale id.</summary>
internal sealed class SqsJournalQueue(IAmazonSQS sqs, string queueUrl) : IJournalQueue
{
    public Task SendAsync(Sale sale, CancellationToken ct) =>
        sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrl,
            MessageBody = JsonSerializer.Serialize(new SaleMessage(sale.SaleId, sale.VenueId, sale.TillId), StrictJson.Options),
            MessageGroupId = $"{sale.VenueId}-{sale.TillId}",
            MessageDeduplicationId = sale.SaleId,
        }, ct);
}

internal interface IMessageVisibility
{
    /// <summary>Hides a received message for the given delay, after which SQS delivers it again.</summary>
    Task DelayAsync(string receiptHandle, TimeSpan delay, CancellationToken ct);
}

internal sealed class SqsMessageVisibility(IAmazonSQS sqs, string queueUrl) : IMessageVisibility
{
    public Task DelayAsync(string receiptHandle, TimeSpan delay, CancellationToken ct) =>
        sqs.ChangeMessageVisibilityAsync(new ChangeMessageVisibilityRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle,
            VisibilityTimeout = (int)Math.Ceiling(delay.TotalSeconds),
        }, ct);
}
