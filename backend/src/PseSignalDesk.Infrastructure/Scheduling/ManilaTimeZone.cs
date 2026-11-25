namespace PseSignalDesk.Infrastructure.Scheduling;

/// <summary>
/// Resolves the Philippine time zone across Windows/Linux/macOS and exposes PHT
/// clock helpers. Falls back to a fixed UTC+8 zone (the Philippines has no DST).
/// </summary>
public static class ManilaTimeZone
{
    public static TimeZoneInfo Instance { get; } = Resolve();

    public static DateTimeOffset NowPht() =>
        TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Instance);

    public static DateOnly TodayPht() =>
        DateOnly.FromDateTime(NowPht().DateTime);

    private static TimeZoneInfo Resolve()
    {
        foreach (var id in new[] { "Asia/Manila", "Singapore Standard Time", "Taipei Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone(
            "PHT",
            TimeSpan.FromHours(8),
            "Philippine Standard Time",
            "PHT");
    }
}
