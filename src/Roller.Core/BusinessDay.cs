namespace Roller.Core;

public static class BusinessDay
{
    // Trading days run 05:00 to 05:00 local time, so sales after midnight count towards the evening's Z-report.
    private static readonly TimeSpan DayStart = TimeSpan.FromHours(5);

    public static DateOnly For(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime - DayStart);

    /// <summary>Local calendar date of the sale. VAT rate changes apply from this date, whatever the trading day.</summary>
    public static DateOnly SupplyDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
