using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Roller.Core;

namespace Roller.Api;

/// <summary>Maps the caller's API key id to its venue. Anyone holding a venue's key can act as that venue.</summary>
internal sealed class VenueKeys(IReadOnlyDictionary<string, string> venueByKeyId)
{
    // VENUE_API_KEYS holds the tills' keys; DEMO_API_KEYS, when set, the public demo page's keys with lower limits.
    public static VenueKeys FromEnvironment()
    {
        var venueByKeyId = new Dictionary<string, string>();
        AddKeys(venueByKeyId, "VENUE_API_KEYS", Env.Required("VENUE_API_KEYS"));
        if (Environment.GetEnvironmentVariable("DEMO_API_KEYS") is { Length: > 0 } demoKeys)
        {
            AddKeys(venueByKeyId, "DEMO_API_KEYS", demoKeys);
        }

        return new VenueKeys(venueByKeyId);
    }

    public bool TryGetVenue(string? apiKeyId, [NotNullWhen(true)] out string? venueId)
    {
        venueId = null;
        return apiKeyId is not null && venueByKeyId.TryGetValue(apiKeyId, out venueId);
    }

    private static void AddKeys(Dictionary<string, string> venueByKeyId, string name, string json)
    {
        var keyIdByVenue = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException($"{name} is not a JSON object: {json}");
        foreach (var (venueId, keyId) in keyIdByVenue)
        {
            if (!venueByKeyId.TryAdd(keyId, venueId))
            {
                throw new InvalidOperationException($"API key id {keyId} in {name} is already mapped to {venueByKeyId[keyId]}");
            }
        }
    }
}
