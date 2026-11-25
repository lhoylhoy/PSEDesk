namespace PseSignalDesk.Domain.Services;

using PseSignalDesk.Domain.Enums;

/// <summary>
/// PSE trading session phases and the Philippine statutory holiday calendar. The
/// scheduler uses it to avoid firing on exchange holidays and the infrastructure uses
/// it to shorten cache lifetimes while the market is open.
/// </summary>
public static class PseTradingSessionRules
{
    public static readonly TimeOnly PreOpenStart = new(9, 0);
    public static readonly TimeOnly ContinuousOpen = new(9, 30);
    public static readonly TimeOnly MorningClose = new(12, 0);
    public static readonly TimeOnly RecessEnd = new(13, 0);
    public static readonly TimeOnly PreCloseStart = new(14, 45);
    public static readonly TimeOnly RunOffStart = new(14, 50);
    public static readonly TimeOnly MarketClose = new(15, 0);

    /// <summary>
    /// End-of-day batch/settlement window. The daily signal run is scheduled after it.
    /// </summary>
    public static readonly TimeOnly EndOfDaySettlement = new(15, 35);

    /// <summary>
    /// Fixed-date regular holidays and special non-working days. Movable observances
    /// (Holy Week, Eid, last-Monday holidays) live in <see cref="MovableHolidays"/>.
    /// </summary>
    private static readonly (int Month, int Day)[] FixedHolidays =
    {
        (1, 1),   // New Year's Day
        (4, 9),   // Araw ng Kagitingan
        (5, 1),   // Labor Day
        (6, 12),  // Independence Day
        (8, 21),  // Ninoy Aquino Day (special non-working)
        (11, 1),  // All Saints' Day
        (11, 30), // Bonifacio Day
        (12, 8),  // Immaculate Conception (special non-working)
        (12, 25), // Christmas Day
        (12, 30), // Rizal Day
        (12, 31)  // Last day of the year (special non-working)
    };

    /// <summary>
    /// Movable observances published by Proclamation. Kept as an explicit, reviewable
    /// table instead of a lunar calculation: when a new proclamation lands, add the
    /// dates for that year. Missing years simply fall back to the fixed dates.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, DateOnly[]> MovableHolidays =
        new Dictionary<int, DateOnly[]>
        {
            [2025] = new[]
            {
                new DateOnly(2025, 1, 29), // Chinese New Year
                new DateOnly(2025, 4, 17), // Maundy Thursday
                new DateOnly(2025, 4, 18), // Good Friday
                new DateOnly(2025, 4, 19), // Black Saturday
                new DateOnly(2025, 3, 31), // Eid'l Fitr
                new DateOnly(2025, 6, 6),  // Eid'l Adha
                new DateOnly(2025, 8, 25), // National Heroes Day
            },
            [2026] = new[]
            {
                new DateOnly(2026, 2, 17), // Chinese New Year
                new DateOnly(2026, 4, 2),  // Maundy Thursday
                new DateOnly(2026, 4, 3),  // Good Friday
                new DateOnly(2026, 4, 4),  // Black Saturday
                new DateOnly(2026, 3, 20), // Eid'l Fitr (proclaimed estimate)
                new DateOnly(2026, 5, 27), // Eid'l Adha (proclaimed estimate)
                new DateOnly(2026, 8, 31), // National Heroes Day
            },
            [2027] = new[]
            {
                new DateOnly(2027, 2, 6),  // Chinese New Year
                new DateOnly(2027, 3, 25), // Maundy Thursday
                new DateOnly(2027, 3, 26), // Good Friday
                new DateOnly(2027, 3, 27), // Black Saturday
                new DateOnly(2027, 3, 10), // Eid'l Fitr (proclaimed estimate)
                new DateOnly(2027, 5, 17), // Eid'l Adha (proclaimed estimate)
                new DateOnly(2027, 8, 30), // National Heroes Day
            }
        };

    /// <summary>
    /// Every holiday the desk knows about for a year: the fixed dates plus that year's
    /// movable observances.
    /// </summary>
    public static IReadOnlyList<DateOnly> GetHolidays(int year)
    {
        var holidays = new HashSet<DateOnly>(
            FixedHolidays.Select(entry => new DateOnly(year, entry.Month, entry.Day)));

        if (MovableHolidays.TryGetValue(year, out var movable))
        {
            holidays.UnionWith(movable);
        }

        return holidays.OrderBy(date => date).ToList();
    }

    public static bool IsHoliday(DateOnly date) =>
        GetHolidays(date.Year).Contains(date);

    /// <summary>
    /// A trading day is a weekday that is not a declared Philippine holiday.
    /// </summary>
    public static bool IsTradingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !IsHoliday(date);

    /// <summary>
    /// Resolves the exchange phase for a Manila date/time. Holidays and weekends are
    /// always <see cref="PseMarketPhase.CLOSED"/>.
    /// </summary>
    public static PseMarketPhase GetPhase(DateOnly date, TimeOnly timePht)
    {
        if (!IsTradingDay(date))
        {
            return PseMarketPhase.CLOSED;
        }

        if (timePht < PreOpenStart)
        {
            return PseMarketPhase.CLOSED;
        }

        if (timePht < ContinuousOpen)
        {
            return PseMarketPhase.PRE_OPEN;
        }

        if (timePht < MorningClose)
        {
            return PseMarketPhase.MORNING_CONTINUOUS;
        }

        if (timePht < RecessEnd)
        {
            return PseMarketPhase.RECESS;
        }

        if (timePht < PreCloseStart)
        {
            return PseMarketPhase.AFTERNOON_CONTINUOUS;
        }

        if (timePht < RunOffStart)
        {
            return PseMarketPhase.PRE_CLOSE;
        }

        if (timePht < MarketClose)
        {
            return PseMarketPhase.RUN_OFF;
        }

        return PseMarketPhase.CLOSED;
    }

    /// <summary>
    /// True while the exchange is in (or just past) a live session, which is the window
    /// where disclosure caches must be refreshed aggressively.
    /// </summary>
    public static bool IsLiveSession(DateOnly date, TimeOnly timePht) =>
        IsTradingDay(date) &&
        timePht >= PreOpenStart &&
        timePht < EndOfDaySettlement;
}
