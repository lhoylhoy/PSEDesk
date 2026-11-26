namespace PseSignalDesk.WebApi.Services;

/// <summary>
/// The request bounds that keep one HTTP call from becoming an unbounded download. A rate limit
/// bounds how often a caller can ask; these bounds limit how much one ask costs, so thirty
/// hourly calls cannot turn into a hundred years of daily bars per symbol.
/// </summary>
public static class DeskRequestLimits
{
    /// <summary>Smallest accepted history window, in calendar days.</summary>
    public const int MinLookbackCalendarDays = 1;

    /// <summary>
    /// Largest accepted history window, in calendar days (five years). The desk's replay and
    /// training both refit on a rolling 60-session window and publish at most a multi-year
    /// equity curve; anything beyond five years is archive archaeology that only inflates the
    /// upstream download and the CPU burn.
    /// </summary>
    public const int MaxLookbackCalendarDays = 1_825;

    /// <summary>
    /// True when <paramref name="lookbackDays"/> is absent (the service default applies) or inside
    /// the accepted range. A negative or zero window used to be silently lifted to the minimum,
    /// which hid a caller's mistake instead of answering <c>400</c>.
    /// </summary>
    public static bool IsLookbackAllowed(int? lookbackDays) =>
        lookbackDays is null ||
        (lookbackDays >= MinLookbackCalendarDays && lookbackDays <= MaxLookbackCalendarDays);

    /// <summary>The operator-facing explanation shared by the backtest and training endpoints.</summary>
    public static string LookbackRequirement =>
        $"lookbackDays must be between {MinLookbackCalendarDays} and {MaxLookbackCalendarDays} " +
        "calendar days (five years). Omit it to use the desk's own default.";
}
