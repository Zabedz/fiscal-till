using System.Globalization;
using System.Text;
using Roller.Core;
using static System.Net.WebUtility;

namespace Roller.Journal;

/// <summary>The customer receipt as HTML, formatted for the market's locale.</summary>
internal static class ReceiptDocument
{
    public static string Render(Sale sale, Venue venue, IMarketRules rules, int receiptNumber)
    {
        var signature = sale.Signature ?? throw new ArgumentException($"Sale {sale.SaleId} has no signature", nameof(sale));
        var culture = CultureInfo.GetCultureInfo(rules.Locale);
        string Money(long cents) => HtmlEncode((cents / 100m).ToString("C", culture));
        string Local(DateTimeOffset instant) => HtmlEncode(TimeZoneInfo.ConvertTime(instant, venue.Zone).ToString("G", culture));

        var html = new StringBuilder();
        html.Append($"""
            <!doctype html>
            <html lang="{culture.TwoLetterISOLanguageName}">
            <meta charset="utf-8">
            <title>Receipt {HtmlEncode(sale.VenueId)}-{HtmlEncode(sale.TillId)} #{receiptNumber}</title>
            <body style="font-family: ui-monospace, monospace; max-width: 34rem; margin: 2rem auto; padding: 0 1rem">
            <h1>{HtmlEncode(venue.Name)}</h1>
            <p>Receipt {HtmlEncode(sale.VenueId)}-{HtmlEncode(sale.TillId)} #{receiptNumber:D8}<br>{Local(sale.CreatedAt)}<br>Sale {sale.SaleId}</p>
            <table style="width: 100%">
            """);
        foreach (var line in sale.Priced.Lines)
        {
            html.Append($"""<tr><td>{line.Quantity} x {HtmlEncode(line.Description)}</td><td style="text-align: right">{Money(line.GrossCents)}</td></tr>""");
        }
        html.Append("""</table><table style="width: 100%; margin-top: 1rem"><tr><th>VAT</th><th>Net</th><th>VAT</th><th>Gross</th></tr>""");
        foreach (var total in sale.Priced.VatByRate)
        {
            html.Append($"""<tr><td>{total.Rate.ToString(culture)} %</td><td>{Money(total.NetCents)}</td><td>{Money(total.VatCents)}</td><td>{Money(total.GrossCents)}</td></tr>""");
        }
        html.Append($"""
            </table>
            <p><strong>Total {Money(sale.Priced.GrossCents)}</strong><br>Paid by {HtmlEncode(sale.Payment.Method)}</p>
            <h2>Fiscal signature</h2>
            <p>Device {HtmlEncode(signature.DeviceId)}, transaction {signature.Counter}<br>Signed {Local(signature.SignedAt)}</p>
            <p><code style="word-break: break-all">{HtmlEncode(signature.Value)}</code></p>
            <p>Signed data: <code style="word-break: break-all">{HtmlEncode(rules.FiscalPayload(sale, venue))}</code></p>
            <p><small>Mock fiscal receipt from a demo system; not a valid tax document.</small></p>
            </body>
            </html>
            """);
        return html.ToString();
    }
}
