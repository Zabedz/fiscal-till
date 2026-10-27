using System.Globalization;
using System.Text.RegularExpressions;

namespace Roller.Core;

/// <summary>A venue from config/venues.json. Tills are numbered T01 up to TillCount.</summary>
public sealed partial record Venue(string VenueId, string Name, string Market, string TimeZone, int TillCount)
{
    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    public bool HasTill(string tillId)
    {
        var match = TillIdPattern().Match(tillId);
        return match.Success && int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) is var number && number >= 1 && number <= TillCount;
    }

    [GeneratedRegex(@"^T([0-9]{2})\z")]
    private static partial Regex TillIdPattern();
}

public sealed class VenueDirectory(IReadOnlyList<Venue> venues)
{
    private readonly Dictionary<string, Venue> _byId = venues.ToDictionary(venue => venue.VenueId);

    public IReadOnlyList<Venue> All { get; } = venues;

    public static VenueDirectory Load() => new(StrictJson.ReadConfig<List<Venue>>("venues.json"));

    public Venue Get(string venueId) =>
        _byId.TryGetValue(venueId, out var venue) ? venue : throw new KeyNotFoundException($"Unknown venue {venueId}");
}
