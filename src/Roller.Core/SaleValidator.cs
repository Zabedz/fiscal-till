using System.Net.Mail;

namespace Roller.Core;

public static class SaleValidator
{
    private const int MaxLines = 50;
    private const int MaxQuantity = 99;
    private const long MaxUnitPriceCents = 1_000_000;
    private const int MaxSkuLength = 32;
    private const int MaxDescriptionLength = 80;
    private const int MaxEmailLength = 254;
    private const int MaxEchoLength = 32;

    public static IReadOnlyList<string> Validate(SaleRequest sale, Venue venue, IMarketRules rules, DateOnly supplyDate)
    {
        // JSON nullability checks do not reach list elements, so a [null] line arrives here.
        if (sale.Lines.Count is 0 or > MaxLines || sale.Lines.Any(line => line is null))
        {
            return [$"lines must hold 1 to {MaxLines} sale lines"];
        }

        var errors = new List<string>();

        if (!venue.HasTill(sale.TillId))
        {
            errors.Add($"tillId {Echo(sale.TillId)} is not a till of {venue.VenueId} (T01 to T{venue.TillCount:D2})");
        }

        for (var i = 0; i < sale.Lines.Count; i++)
        {
            var line = sale.Lines[i];
            if (string.IsNullOrWhiteSpace(line.Sku) || line.Sku.Length > MaxSkuLength)
            {
                errors.Add($"lines[{i}].sku must be 1 to {MaxSkuLength} characters");
            }
            if (line.Description.Length > MaxDescriptionLength)
            {
                errors.Add($"lines[{i}].description must be at most {MaxDescriptionLength} characters");
            }
            if (line.Quantity is < 1 or > MaxQuantity)
            {
                errors.Add($"lines[{i}].quantity must be 1 to {MaxQuantity}");
            }
            if (line.UnitPriceCents is < 0 or > MaxUnitPriceCents)
            {
                errors.Add($"lines[{i}].unitPriceCents must be 0 to {MaxUnitPriceCents}");
            }
            if (!rules.TryGetRate(line.Category, supplyDate, out _))
            {
                errors.Add($"lines[{i}].category {Echo(line.Category)} has no {rules.Market} VAT rate on {supplyDate:yyyy-MM-dd}");
            }
        }

        if (sale.Payment.Method is not ("cash" or "card"))
        {
            errors.Add("payment.method must be cash or card");
        }

        if (errors.Count == 0)
        {
            var total = sale.Lines.Sum(line => line.UnitPriceCents * line.Quantity);
            if (sale.Payment.AmountCents != total)
            {
                errors.Add($"payment.amountCents {sale.Payment.AmountCents} does not match the sale total {total}");
            }
        }

        if (sale.CustomerEmail is { } email && !IsEmailAddress(email))
        {
            errors.Add("customerEmail is not a valid address");
        }

        return errors;
    }

    private static string Echo(string value) => value.Length <= MaxEchoLength ? value : $"{value[..MaxEchoLength]}...";

    private static bool IsEmailAddress(string email) =>
        email.Length <= MaxEmailLength && MailAddress.TryCreate(email, out var parsed) && parsed.Address == email;
}
