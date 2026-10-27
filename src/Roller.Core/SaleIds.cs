using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Roller.Core;

public static partial class SaleIds
{
    // Omitting nulls keeps the hash of an existing request stable when an optional field is added later.
    private static readonly JsonSerializerOptions HashJson = new(StrictJson.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Deterministic, so a till's retry with the same Idempotency-Key maps to the same sale.</summary>
    public static string For(string venueId, string idempotencyKey) => Sha256($"{venueId}\n{idempotencyKey}")[..32];

    public static bool IsWellFormed(string saleId) => SaleIdPattern().IsMatch(saleId);

    /// <summary>Hash of the parsed request, so JSON formatting differences between retries do not count.</summary>
    public static string RequestHash(SaleRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, HashJson)));

    private static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex(@"^[0-9a-f]{32}\z")]
    private static partial Regex SaleIdPattern();
}
