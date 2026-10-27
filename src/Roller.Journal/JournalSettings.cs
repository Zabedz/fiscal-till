namespace Roller.Journal;

/// <summary>Read from environment variables; scripts/journal.sh sets them from the deployed stack and .env.</summary>
internal sealed record JournalSettings(
    string QueueUrl, string SalesTable, string ReceiptsBucket, string ReceiptSender, string ConnectionString, int Consumers)
{
    // Measured at about 2 sales a second each from a laptop, so 16 keep up with the slow drill's 20 a second.
    private const int DefaultConsumers = 16;

    public static JournalSettings From(IConfiguration config) => new(
        Required(config, "JOURNAL_QUEUE_URL"),
        Required(config, "SALES_TABLE"),
        Required(config, "RECEIPTS_BUCKET"),
        Required(config, "RECEIPT_SENDER"),
        Required(config, "JOURNAL_DB"),
        ParseConsumers(config["JOURNAL_CONSUMERS"]));

    private static string Required(IConfiguration config, string name) =>
        config[name] is { } value && !string.IsNullOrWhiteSpace(value) && value != "None"
            ? value
            : throw new InvalidOperationException($"{name} is not set; run the worker through scripts/journal.sh");

    private static int ParseConsumers(string? value) =>
        value is null ? DefaultConsumers
        : int.TryParse(value, out var consumers) && consumers > 0 ? consumers
        : throw new InvalidOperationException($"JOURNAL_CONSUMERS must be a positive number, got '{value}'");
}
