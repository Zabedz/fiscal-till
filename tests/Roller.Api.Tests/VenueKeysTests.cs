namespace Roller.Api.Tests;

// Sets process environment variables; xUnit runs one class's tests in turn, and no other test reads these two.
public class VenueKeysTests : IDisposable
{
    public VenueKeysTests()
    {
        Environment.SetEnvironmentVariable("VENUE_API_KEYS", """{"paris-01":"key-paris","berlin-01":"key-berlin"}""");
        Environment.SetEnvironmentVariable("DEMO_API_KEYS", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("VENUE_API_KEYS", null);
        Environment.SetEnvironmentVariable("DEMO_API_KEYS", null);
    }

    [Fact]
    public void DemoKeysAreOptional()
    {
        var keys = VenueKeys.FromEnvironment();

        Assert.True(keys.TryGetVenue("key-paris", out var venue));
        Assert.Equal("paris-01", venue);
        Assert.False(keys.TryGetVenue("demo-paris", out _));
    }

    [Fact]
    public void DemoKeysMapToTheSameVenues()
    {
        Environment.SetEnvironmentVariable("DEMO_API_KEYS", """{"paris-01":"demo-paris"}""");

        var keys = VenueKeys.FromEnvironment();

        Assert.True(keys.TryGetVenue("demo-paris", out var venue));
        Assert.Equal("paris-01", venue);
        Assert.True(keys.TryGetVenue("key-berlin", out _));
    }

    [Fact]
    public void AKeyIdMappedTwiceIsRefused()
    {
        Environment.SetEnvironmentVariable("DEMO_API_KEYS", """{"vienna-01":"key-paris"}""");

        var error = Assert.Throws<InvalidOperationException>(VenueKeys.FromEnvironment);
        Assert.Contains("key-paris", error.Message);
    }
}
