namespace Roller.Fiscalise;

internal static class Backoff
{
    private const int BaseSeconds = 5;
    private const int CapSeconds = 60;
    private const int MaxDoublings = 10;
    private const double MaxJitter = 0.2;

    /// <summary>Delay after a failed delivery: 5 s doubling per receive up to 60 s, plus up to 20% jitter.</summary>
    public static TimeSpan After(int receiveCount, Random random)
    {
        var doublings = Math.Clamp(receiveCount - 1, 0, MaxDoublings);
        var seconds = Math.Min(BaseSeconds * (1 << doublings), CapSeconds);
        return TimeSpan.FromSeconds(seconds * (1 + random.NextDouble() * MaxJitter));
    }
}
