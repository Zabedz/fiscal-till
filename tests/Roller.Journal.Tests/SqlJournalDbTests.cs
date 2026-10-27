using System.Security.Cryptography;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Roller.Journal.Tests;

/// <summary>Runs dbo.RecordSale against the Docker SQL Server; skipped unless JOURNAL_DB holds a connection string.</summary>
public class SqlJournalDbTests
{
    private const int ConcurrentSales = 50;

    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("JOURNAL_DB");

    [Fact]
    public async Task ConcurrentSalesOnOneTillGetGaplessNumbers()
    {
        Assert.SkipWhen(ConnectionString is null, "JOURNAL_DB is not set; start the database with scripts/journal-db.sh up");
        var venue = NewVenueId();
        var journal = new SqlJournalDb(ConnectionString!);

        var numbers = await Task.WhenAll(Enumerable.Range(0, ConcurrentSales).Select(_ =>
            Task.Run(() => journal.RecordAsync(TestSales.Signed(NewSaleId(), venue, "T01"), TestContext.Current.CancellationToken))));

        Assert.Equal(Enumerable.Range(1, ConcurrentSales), numbers.Select(recorded => recorded.ReceiptNumber).Order());
        Assert.All(numbers, recorded => Assert.False(recorded.AlreadyRecorded));
        Assert.Equal(0, await MissingNumbersAsync(venue));
    }

    [Fact]
    public async Task RecordingASaleTwiceKeepsItsNumber()
    {
        Assert.SkipWhen(ConnectionString is null, "JOURNAL_DB is not set; start the database with scripts/journal-db.sh up");
        var venue = NewVenueId();
        var journal = new SqlJournalDb(ConnectionString!);
        var sale = TestSales.Signed(NewSaleId(), venue, "T01");

        var first = await journal.RecordAsync(sale, TestContext.Current.CancellationToken);
        var second = await journal.RecordAsync(sale, TestContext.Current.CancellationToken);
        var next = await journal.RecordAsync(TestSales.Signed(NewSaleId(), venue, "T01"), TestContext.Current.CancellationToken);

        Assert.Equal(new RecordedSale(1, false), first);
        Assert.Equal(new RecordedSale(1, true), second);
        Assert.Equal(2, next.ReceiptNumber);
    }

    [Fact]
    public async Task ReusedOutputVariableStillRecordsANewSale()
    {
        Assert.SkipWhen(ConnectionString is null, "JOURNAL_DB is not set; start the database with scripts/journal-db.sh up");
        var venue = NewVenueId();
        await using var connection = new SqlConnection(ConnectionString);

        // A T-SQL caller reusing one variable: the second sale must get number 2.
        var second = await connection.QuerySingleAsync<(int ReceiptNo, bool AlreadyRecorded)>($"""
            DECLARE @n int, @already bit;
            EXEC dbo.RecordSale @SaleId = '{NewSaleId()}', @VenueId = N'{venue}', @TillId = N'T01', @Market = 'FR', @Currency = 'EUR',
                @BusinessDate = '2026-10-27', @SoldAt = '2026-10-27T11:30:00+00:00', @GrossCents = 100, @VatCents = 9, @PaymentMethod = 'card',
                @SignatureDeviceId = N'd', @SignatureCounter = 1, @SignatureValue = N's', @SignedAt = '2026-10-27T11:30:01+00:00',
                @LinesJson = N'[]', @ReceiptNo = @n OUTPUT, @AlreadyRecorded = @already OUTPUT;
            EXEC dbo.RecordSale @SaleId = '{NewSaleId()}', @VenueId = N'{venue}', @TillId = N'T01', @Market = 'FR', @Currency = 'EUR',
                @BusinessDate = '2026-10-27', @SoldAt = '2026-10-27T11:31:00+00:00', @GrossCents = 100, @VatCents = 9, @PaymentMethod = 'card',
                @SignatureDeviceId = N'd', @SignatureCounter = 2, @SignatureValue = N's', @SignedAt = '2026-10-27T11:31:01+00:00',
                @LinesJson = N'[]', @ReceiptNo = @n OUTPUT, @AlreadyRecorded = @already OUTPUT;
            SELECT @n AS ReceiptNo, @already AS AlreadyRecorded;
            """);

        Assert.Equal((2, false), second);
    }

    [Fact]
    public async Task OverLongVenueIdIsRejected()
    {
        Assert.SkipWhen(ConnectionString is null, "JOURNAL_DB is not set; start the database with scripts/journal-db.sh up");
        var journal = new SqlJournalDb(ConnectionString!);
        var sale = TestSales.Signed(NewSaleId(), NewVenueId() + "-with-a-long-suffix", "T01");

        var error = await Assert.ThrowsAsync<SqlException>(() => journal.RecordAsync(sale, TestContext.Current.CancellationToken));
        Assert.Equal(StringTruncated, error.Number);
    }

    [Fact]
    public async Task ZReportStepSpansTradingDaysAndTimestampsKeepFullPrecision()
    {
        Assert.SkipWhen(ConnectionString is null, "JOURNAL_DB is not set; start the database with scripts/journal-db.sh up");
        var venue = NewVenueId();
        var journal = new SqlJournalDb(ConnectionString!);
        var day1 = new DateOnly(2026, 10, 27);
        var day2 = new DateOnly(2026, 10, 28);
        var preciseSoldAt = new DateTimeOffset(2026, 10, 27, 21, 59, 59, TimeSpan.Zero).AddTicks(9_996_000);

        // Receipt 3 belongs to the next trading day, as after a DLQ redrive; day 1's report holds 1, 2 and 4.
        foreach (var (date, soldAt) in new[] { (day1, preciseSoldAt), (day1, preciseSoldAt), (day2, preciseSoldAt), (day1, preciseSoldAt) })
        {
            await journal.RecordAsync(
                TestSales.Signed(NewSaleId(), venue, "T01") with { BusinessDate = date, CreatedAt = soldAt },
                TestContext.Current.CancellationToken);
        }

        await using var connection = new SqlConnection(ConnectionString);
        await using var report = await connection.QueryMultipleAsync(
            "dbo.ZReport", new { VenueId = venue, BusinessDate = day1.ToDateTime(TimeOnly.MinValue) }, commandType: System.Data.CommandType.StoredProcedure);
        await report.ReadAsync();
        var receipts = (await report.ReadAsync<(string TillId, int ReceiptNo, string SaleId, DateTimeOffset SoldAt, long GrossCents, long RunningGrossCents, int? StepFromPrevious)>()).ToList();

        Assert.Equal(new[] { 1, 2, 4 }, receipts.Select(receipt => receipt.ReceiptNo));
        Assert.Equal(new int?[] { null, 1, 1 }, receipts.Select(receipt => receipt.StepFromPrevious));
        Assert.All(receipts, receipt => Assert.Equal(preciseSoldAt, receipt.SoldAt));
    }

    private const int StringTruncated = 2628;

    private static async Task<int> MissingNumbersAsync(string venue)
    {
        await using var connection = new SqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT MissingNumbers FROM dbo.ReceiptGaps WHERE TillKey = @TillKey", new { TillKey = $"{venue}-T01" });
    }

    // Each test uses its own venue id, so its till starts at receipt 1 on a database that already holds other runs.
    private static string NewVenueId() => $"test-{RandomNumberGenerator.GetHexString(8, lowercase: true)}";

    private static string NewSaleId() => RandomNumberGenerator.GetHexString(32, lowercase: true);
}
