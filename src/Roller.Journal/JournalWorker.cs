using Amazon.SQS;
using Amazon.SQS.Model;

namespace Roller.Journal;

/// <summary>Long-polls the FIFO journal queue with several consumers, one message at a time each.</summary>
internal sealed class JournalWorker(IAmazonSQS sqs, JournalSettings settings, JournalProcessor processor, ILogger<JournalWorker> log)
    : BackgroundService
{
    // One per receive: SQS counts every returned message as received, so a batch would dead-letter sales queued behind a failing one.
    private const int MaxMessages = 1;
    private const int WaitSeconds = 20;
    private static readonly TimeSpan PauseAfterQueueError = TimeSpan.FromSeconds(5);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(1, settings.Consumers).Select(_ => ConsumeAsync(stoppingToken)));

    private async Task ConsumeAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = settings.QueueUrl,
                    MaxNumberOfMessages = MaxMessages,
                    WaitTimeSeconds = WaitSeconds,
                }, ct);

                foreach (var message in response.Messages ?? [])
                {
                    await HandleAsync(message, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                // A network or queue fault must leave the consumer running; the message, if any, comes back.
                log.LogWarning("Journal consumer error, pausing: {Error}", e.Message);
                await Task.Delay(PauseAfterQueueError, ct);
            }
        }
    }

    private async Task HandleAsync(Message message, CancellationToken ct)
    {
        JournalOutcome outcome;
        try
        {
            outcome = await processor.ProcessAsync(message.Body, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            log.LogError(e, "Journal message {MessageId} failed; it returns after the visibility timeout", message.MessageId);
            return;
        }

        // Finished work is acknowledged even during shutdown, so it is not repeated.
        if (outcome == JournalOutcome.Done)
        {
            await sqs.DeleteMessageAsync(settings.QueueUrl, message.ReceiptHandle, CancellationToken.None);
        }
        else if (outcome == JournalOutcome.Poison)
        {
            await sqs.ChangeMessageVisibilityAsync(settings.QueueUrl, message.ReceiptHandle, 0, CancellationToken.None);
        }
    }
}
