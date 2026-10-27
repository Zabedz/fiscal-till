using System.Globalization;

namespace Roller.Core.Tests;

public class BusinessDayTests
{
    private static readonly TimeZoneInfo Vienna = TimeZoneInfo.FindSystemTimeZoneById("Europe/Vienna");

    [Theory]
    [InlineData("2026-10-27T23:30:00Z", "2026-10-27")] // 00:30 local on the 28th
    [InlineData("2026-10-28T03:59:00Z", "2026-10-27")] // 04:59 local
    [InlineData("2026-10-28T04:00:00Z", "2026-10-28")] // 05:00 local
    [InlineData("2026-03-29T02:30:00Z", "2026-03-28")] // 04:30 local, the morning clocks went forward
    [InlineData("2026-03-29T03:00:00Z", "2026-03-29")] // 05:00 local
    public void TradingDayStartsAtFiveLocalTime(string utc, string expected)
    {
        Assert.Equal(Date(expected), BusinessDay.For(Instant(utc), Vienna));
    }

    [Fact]
    public void SupplyDateIsTheLocalCalendarDate()
    {
        // 00:30 local on 1 July 2026: still the previous trading day, but the new VAT rates already apply.
        var instant = Instant("2026-06-30T22:30:00Z");

        Assert.Equal(Date("2026-06-30"), BusinessDay.For(instant, Vienna));
        Assert.Equal(Date("2026-07-01"), BusinessDay.SupplyDate(instant, Vienna));
    }

    private static DateTimeOffset Instant(string utc) => DateTimeOffset.Parse(utc, CultureInfo.InvariantCulture);

    private static DateOnly Date(string date) => DateOnly.Parse(date, CultureInfo.InvariantCulture);
}
