using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Roller.Core;

namespace Roller.Journal;

internal sealed record RecordedSale(int ReceiptNumber, bool AlreadyRecorded);

internal interface IJournalDb
{
    /// <summary>Records a signed sale and returns its gapless receipt number; a sale recorded before keeps its number.</summary>
    Task<RecordedSale> RecordAsync(Sale sale, CancellationToken ct);
}

/// <summary>Calls dbo.RecordSale in db/schema.sql, which does the numbering in one transaction.</summary>
internal sealed class SqlJournalDb(string connectionString) : IJournalDb
{
    private const int DeadlockVictim = 1205;
    private const int MaxAttempts = 3;
    private const int DeadlockPauseMs = 200;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<RecordedSale> RecordAsync(Sale sale, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await RecordOnceAsync(sale, ct);
            }
            catch (SqlException e) when (e.Number == DeadlockVictim && attempt < MaxAttempts)
            {
                await Task.Delay(Random.Shared.Next(DeadlockPauseMs, 2 * DeadlockPauseMs) * attempt, ct);
            }
        }
    }

    private async Task<RecordedSale> RecordOnceAsync(Sale sale, CancellationToken ct)
    {
        var signature = sale.Signature ?? throw new ArgumentException($"Sale {sale.SaleId} has no signature", nameof(sale));

        var parameters = new DynamicParameters();
        parameters.Add("SaleId", sale.SaleId);
        parameters.Add("VenueId", sale.VenueId);
        parameters.Add("TillId", sale.TillId);
        parameters.Add("Market", sale.Market);
        parameters.Add("Currency", sale.Currency);
        parameters.Add("BusinessDate", sale.BusinessDate.ToDateTime(TimeOnly.MinValue), DbType.Date);
        parameters.Add("SoldAt", sale.CreatedAt);
        parameters.Add("GrossCents", sale.Priced.GrossCents);
        parameters.Add("VatCents", sale.Priced.VatCents);
        parameters.Add("PaymentMethod", sale.Payment.Method);
        parameters.Add("SignatureDeviceId", signature.DeviceId);
        parameters.Add("SignatureCounter", signature.Counter);
        parameters.Add("SignatureValue", signature.Value);
        parameters.Add("SignedAt", signature.SignedAt);
        parameters.Add("LinesJson", JsonSerializer.Serialize(sale.Priced.Lines, Json));
        parameters.Add("ReceiptNo", dbType: DbType.Int32, direction: ParameterDirection.Output);
        parameters.Add("AlreadyRecorded", dbType: DbType.Boolean, direction: ParameterDirection.Output);

        await using var connection = new SqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition(
            "dbo.RecordSale", parameters, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return new RecordedSale(parameters.Get<int>("ReceiptNo"), parameters.Get<bool>("AlreadyRecorded"));
    }
}
