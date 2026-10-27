// Venue list read from config/venues.json at synth time; TillStack creates one API key per venue from it.

using System.Text.Json;

namespace Roller.Infra;

public sealed record VenueConfig(string VenueId, string Name, string Market, string TimeZone, int TillCount)
{
    public static IReadOnlyList<VenueConfig> Load(string path)
    {
        var venues = JsonSerializer.Deserialize<List<VenueConfig>>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                RespectNullableAnnotations = true,
                RespectRequiredConstructorParameters = true,
            });

        return venues is { Count: > 0 } ? venues : throw new InvalidDataException($"No venues found in {path}");
    }
}
