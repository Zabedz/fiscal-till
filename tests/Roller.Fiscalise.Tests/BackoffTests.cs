namespace Roller.Fiscalise.Tests;

public class BackoffTests
{
    // Must match FiscaliseMaxReceives in infra/Roller.Infra/TillStack.cs.
    private const int MaxReceives = 10;

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(10, 60)]
    public void DelayDoublesUpToTheCapWithAtMostTwentyPercentJitter(int receiveCount, int baseSeconds)
    {
        var random = new Random(receiveCount);
        for (var sample = 0; sample < 100; sample++)
        {
            Assert.InRange(Backoff.After(receiveCount, random).TotalSeconds, baseSeconds, baseSeconds * 1.2);
        }
    }

    [Fact]
    public void LastAttemptComesAfterAFiveMinuteOutageHasEnded()
    {
        // Jitter only lengthens delays, so the base delays between the first and last delivery are the guaranteed budget.
        var untilLastAttempt = Enumerable.Range(1, MaxReceives - 1).Sum(receive => Backoff.After(receive, new NoJitter()).TotalSeconds);

        Assert.True(untilLastAttempt > TimeSpan.FromMinutes(5).TotalSeconds, $"only {untilLastAttempt:0} s of retries");
    }

    private sealed class NoJitter : Random
    {
        public override double NextDouble() => 0;
    }
}
