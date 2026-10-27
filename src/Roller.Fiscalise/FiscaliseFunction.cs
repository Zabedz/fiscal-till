using Amazon.Lambda.Core;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.Lambda.SQSEvents;

[assembly: LambdaSerializer(typeof(DefaultLambdaJsonSerializer))]

namespace Roller.Fiscalise;

public sealed class FiscaliseFunction
{
    // Reports every message failed until signing exists, so unsigned sales end in the DLQ and raise the alarm.
    public SQSBatchResponse Handle(SQSEvent sqsEvent, ILambdaContext context)
    {
        var failures = sqsEvent.Records
            .Select(message => new SQSBatchResponse.BatchItemFailure { ItemIdentifier = message.MessageId })
            .ToList();

        context.Logger.LogWarning("Signing not implemented; returning {Count} messages to the queue", failures.Count);
        return new SQSBatchResponse { BatchItemFailures = failures };
    }
}
