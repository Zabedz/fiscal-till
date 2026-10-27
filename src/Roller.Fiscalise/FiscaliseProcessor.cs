using System.Diagnostics;
using System.Text.Json;
using Amazon.Lambda.Core;
using Roller.Core;

namespace Roller.Fiscalise;

internal sealed record QueueMessage(string MessageId, string ReceiptHandle, string Body, int ReceiveCount);

/// <summary>Signs one sale per message. True means the message is done and can be deleted.</summary>
internal sealed class FiscaliseProcessor(
    ISaleStore sales,
    VenueDirectory venues,
    MarketRulesRegistry markets,
    IFiscalPartner partner,
    IJournalQueue journal,
    IMessageVisibility visibility,
    Random random)
{
    public async Task<bool> ProcessAsync(QueueMessage message, ILambdaLogger log, CancellationToken ct)
    {
        try
        {
            return await SignAsync(message, log, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Only this message is retried, with backoff, and dead-lettered if the fault persists.
            log.LogError("Message {MessageId} failed: {Error}", message.MessageId, e.ToString());
            await RetryLaterAsync(message, log, ct);
            return false;
        }
    }

    private async Task<bool> SignAsync(QueueMessage message, ILambdaLogger log, CancellationToken ct)
    {
        if (Parse(message.Body) is not { } request)
        {
            log.LogError("Poison message {MessageId}: {Body}", message.MessageId, message.Body);
            return await DeadLetterSoonAsync(message, log, ct);
        }

        var sale = await sales.GetAsync(request.SaleId, ct);
        if (sale is null)
        {
            log.LogError("Message {MessageId} names unknown sale {SaleId}", message.MessageId, request.SaleId);
            return await DeadLetterSoonAsync(message, log, ct);
        }

        switch (sale.Status)
        {
            case SaleStatus.Failed:
                return true;
            case SaleStatus.Signed:
                // A crash after signing may have lost the journal hand-off; the journal keeps one entry per sale id.
                await journal.SendAsync(sale, ct);
                return true;
        }

        var payload = markets.For(sale.Market).FiscalPayload(sale, venues.Get(sale.VenueId));
        var result = await partner.SignAsync(sale, payload, ct);
        switch (result)
        {
            case PartnerResult.Signed(var signature):
                if (!await sales.MarkSignedAsync(sale, signature, ct) && (await sales.GetAsync(sale.SaleId, ct))?.Status != SaleStatus.Signed)
                {
                    throw new InvalidOperationException($"Sale {sale.SaleId} changed while it was being signed");
                }
                await journal.SendAsync(sale, ct);
                log.LogInformation("Signed {SaleId} as {DeviceId} #{Counter}", sale.SaleId, signature.DeviceId, signature.Counter);
                return true;

            case PartnerResult.Rejected(var reason):
                await sales.MarkFailedAsync(sale, reason, ct);
                log.LogError("Partner rejected {SaleId}: {Reason}", sale.SaleId, reason);
                return true;

            case PartnerResult.Unavailable(var reason):
                log.LogWarning("Partner unavailable for {SaleId} (delivery {ReceiveCount}): {Reason}", sale.SaleId, message.ReceiveCount, reason);
                await RetryLaterAsync(message, log, ct);
                return false;

            default:
                throw new UnreachableException($"Unhandled partner result {result.GetType().Name}");
        }
    }

    private async Task RetryLaterAsync(QueueMessage message, ILambdaLogger log, CancellationToken ct) =>
        await SetVisibilityAsync(message, Backoff.After(message.ReceiveCount, random), log, ct);

    // Visible again at once, so the message reaches maxReceiveCount and the DLQ within seconds.
    private async Task<bool> DeadLetterSoonAsync(QueueMessage message, ILambdaLogger log, CancellationToken ct)
    {
        await SetVisibilityAsync(message, TimeSpan.Zero, log, ct);
        return false;
    }

    private async Task SetVisibilityAsync(QueueMessage message, TimeSpan delay, ILambdaLogger log, CancellationToken ct)
    {
        try
        {
            await visibility.DelayAsync(message.ReceiptHandle, delay, ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // The queue's own visibility timeout still brings the message back.
            log.LogWarning("Could not delay {MessageId}: {Error}", message.MessageId, e.Message);
        }
    }

    private static SaleMessage? Parse(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<SaleMessage>(body, StrictJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
