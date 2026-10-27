using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Roller.Core;

namespace Roller.Api;

/// <summary>Maps the caller's API key id to its venue. Anyone holding a venue's key can act as that venue.</summary>
internal sealed class VenueKeys(IReadOnlyDictionary<string, string> venueByKeyId)
{
    public static VenueKeys FromEnvironment()
    {
        var json = Env.Required("VENUE_API_KEYS");
        var keyIdByVenue = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException($"VENUE_API_KEYS is not a JSON object: {json}");

        return new VenueKeys(keyIdByVenue.ToDictionary(pair => pair.Value, pair => pair.Key));
    }

    public bool TryGetVenue(string? apiKeyId, [NotNullWhen(true)] out string? venueId)
    {
        venueId = null;
        return apiKeyId is not null && venueByKeyId.TryGetValue(apiKeyId, out venueId);
    }
}
