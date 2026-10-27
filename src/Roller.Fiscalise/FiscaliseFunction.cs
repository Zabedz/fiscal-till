using System.Globalization;
using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.SQS;
using Roller.Core;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace Roller.Fiscalise;

public sealed class FiscaliseFunction
{
    private static readonly TimeSpan TimeoutMargin = TimeSpan.FromSeconds(1);

    private readonly FiscaliseProcessor _processor;

    public FiscaliseFunction()
        : this(FromEnvironment())
    {
    }

    internal FiscaliseFunction(FiscaliseProcessor processor) => _processor = processor;

    public async Task<SQSBatchResponse> Handle(SQSEvent sqsEvent, ILambdaContext context)
    {
        var budget = context.RemainingTime - TimeoutMargin;
        using var timeout = new CancellationTokenSource(budget > TimeSpan.Zero ? budget : TimeSpan.Zero);

        var results = await Task.WhenAll(sqsEvent.Records.Select(async record =>
        {
            try
            {
                return (record.MessageId, Done: await _processor.ProcessAsync(ToQueueMessage(record), context.Logger, timeout.Token));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // Out of time: report only this message, so the finished ones in the batch are still deleted.
                return (record.MessageId, Done: false);
            }
        }));

        return new SQSBatchResponse
        {
            BatchItemFailures = results
                .Where(result => !result.Done)
                .Select(result => new SQSBatchResponse.BatchItemFailure { ItemIdentifier = result.MessageId })
                .ToList(),
        };
    }

    private static QueueMessage ToQueueMessage(SQSEvent.SQSMessage record) => new(
        record.MessageId,
        record.ReceiptHandle,
        record.Body,
        int.Parse(record.Attributes["ApproximateReceiveCount"], CultureInfo.InvariantCulture));

    private static FiscaliseProcessor FromEnvironment()
    {
        var sqs = new AmazonSQSClient();
        var apiKey = new AmazonSecretsManagerClient()
            .GetSecretValueAsync(new GetSecretValueRequest { SecretId = Env.Required("PARTNER_KEY_SECRET_ARN") })
            .GetAwaiter().GetResult().SecretString;

        return new FiscaliseProcessor(
            DynamoSaleStore.FromEnvironment(),
            VenueDirectory.Load(),
            MarketRulesRegistry.Load(),
            new PartnerClient(new HttpClient { BaseAddress = new Uri(Env.Required("PARTNER_URL")) }, apiKey),
            new SqsJournalQueue(sqs, Env.Required("JOURNAL_QUEUE_URL")),
            new SqsMessageVisibility(sqs, Env.Required("FISCALISE_QUEUE_URL")),
            Random.Shared);
    }
}
