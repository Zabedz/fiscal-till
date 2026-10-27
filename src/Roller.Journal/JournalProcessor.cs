using System.Globalization;
using System.Text.Json;
using Amazon.Runtime;
using Roller.Core;

namespace Roller.Journal;

internal enum JournalOutcome
{
    /// <summary>Recorded; delete the message.</summary>
    Done,

    /// <summary>A fault that may clear; leave the message for redelivery.</summary>
    Retry,

    /// <summary>The message can never succeed; return it at once so it reaches the DLQ.</summary>
    Poison,
}

/// <summary>Journals one signed sale. Each step is safe to repeat, so a redelivery after a crash finishes the work.</summary>
internal sealed class JournalProcessor(
    ISaleStore sales,
    VenueDirectory venues,
    MarketRulesRegistry markets,
    IJournalDb journal,
    IReceiptStore receipts,
    IReceiptMailer mailer,
    TimeProvider clock,
    ILogger<JournalProcessor> log)
{
    public async Task<JournalOutcome> ProcessAsync(string body, CancellationToken ct)
    {
        if (Parse(body) is not { } message)
        {
            log.LogError("Poison journal message: {Body}", body);
            return JournalOutcome.Poison;
        }

        var sale = await sales.GetAsync(message.SaleId, ct);
        if (sale is not { Status: SaleStatus.Signed, Signature: not null })
        {
            log.LogError("Sale {SaleId} cannot be journaled: {Status}", message.SaleId, sale?.Status.ToString() ?? "not found");
            return JournalOutcome.Poison;
        }

        var venue = venues.Get(sale.VenueId);
        var rules = markets.For(sale.Market);
        var recorded = await journal.RecordAsync(sale, ct);

        var key = string.Create(CultureInfo.InvariantCulture,
            $"{sale.VenueId}/{sale.BusinessDate:yyyy-MM-dd}/{sale.TillId}/{recorded.ReceiptNumber:D8}.html");
        var html = ReceiptDocument.Render(sale, venue, rules, recorded.ReceiptNumber);
        await receipts.PutAsync(key, html, sale.SaleId, ct);

        if (!await sales.MarkJournaledAsync(sale.SaleId, new Receipt(recorded.ReceiptNumber, key, clock.GetUtcNow()), ct))
        {
            throw new InvalidOperationException(
                $"Sale {sale.SaleId} holds a receipt number other than journal number {recorded.ReceiptNumber}; the journal database may have been reset");
        }

        log.LogInformation("Journaled {SaleId} as {VenueId}-{TillId} #{ReceiptNumber}{Replay}",
            sale.SaleId, sale.VenueId, sale.TillId, recorded.ReceiptNumber, recorded.AlreadyRecorded ? " (already recorded)" : "");

        await EmailAsync(sale, venue, html, ct);
        return JournalOutcome.Done;
    }

    // Sent before EmailedAt is set, so a crash in between sends the email twice.
    private async Task EmailAsync(Sale sale, Venue venue, string html, CancellationToken ct)
    {
        if (sale.CustomerEmail is not { } to || sale.EmailedAt is not null)
        {
            return;
        }

        try
        {
            await mailer.SendAsync(to, $"Your receipt from {venue.Name}", html, ct);
            await sales.MarkEmailedAsync(sale.SaleId, clock.GetUtcNow(), CancellationToken.None);
            log.LogInformation("Emailed receipt for {SaleId}", sale.SaleId);
        }
        catch (AmazonServiceException e)
        {
            // Best effort: the journal entry and receipt stand. The error code only, since SES messages can name the address.
            log.LogWarning("Receipt email for {SaleId} failed: {ErrorCode}", sale.SaleId, e.ErrorCode);
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
