namespace PseSignalDesk.Domain.Services;

using PseSignalDesk.Domain.Enums;

/// <summary>
/// The live drift governor: how much new risk a promoted model may take once its own graded
/// scorecard starts disagreeing with the record it was promoted on.
/// <para>
/// The thresholds are expressed in <b>standard errors</b>, not in raw percentage points. With
/// only fifteen to thirty graded picks the standard error of a 45% win rate is 11 to 13 points
/// and the standard error of a mean R is 0.27 to 0.39R, so a fixed "10pp / 0.25R" trigger would
/// fire on pure noise and halt a healthy book roughly a third of the time. Entering a throttle
/// costs real money (it is a one-way door until the book proves itself again), so the governor
/// demands a two-sigma move, takes the larger of that and a practical floor, and will not
/// release a throttle until the gap has halved.
/// </para>
/// </summary>
public static class LiveDriftPolicy
{
    /// <summary>Graded picks needed before a drift verdict is allowed at all.</summary>
    public const int MinimumGradedPicks = 15;

    /// <summary>Practical floor on the win-rate trigger: 20 points below the model's own record.</summary>
    public const decimal MinimumWinRateGapPct = 20m;

    /// <summary>Typical spread of one trade's result in R units, used for the expectancy error bar.</summary>
    public const decimal ExpectancyStandardDeviationR = 1.5m;

    /// <summary>Practical floor on the expectancy trigger: 0.60R below the model's own record.</summary>
    public const decimal MinimumExpectancyGapR = 0.60m;

    /// <summary>Rolling expectancy at or below this is no longer an edge: deep cut.</summary>
    public const decimal DegradedExpectancyR = 0m;

    /// <summary>Rolling expectancy at or below this halts new risk outright.</summary>
    public const decimal HaltedExpectancyR = -0.50m;

    public const decimal StableMultiplier = 1.00m;
    public const decimal WarningMultiplier = 0.65m;
    public const decimal DegradedMultiplier = 0.35m;
    public const decimal HaltedMultiplier = 0.00m;

    /// <summary>Worst-case standard error of a win rate at <c>p = 0.5</c>: <c>0.5 / sqrt(n)</c>.</summary>
    public static decimal WinRateStandardErrorPct(int gradedPicks) => gradedPicks <= 0
        ? 50m
        : decimal.Round(50m / (decimal)Math.Sqrt(gradedPicks), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Win-rate gap that opens a <see cref="LiveDriftStatus.WARNING"/>: two standard errors, but
    /// never less than <see cref="MinimumWinRateGapPct"/>. At 15 picks this is 25.8 points; at
    /// 30 it is 20.
    /// </summary>
    public static decimal RequiredWinRateGapPct(int gradedPicks) =>
        Math.Max(MinimumWinRateGapPct, 2m * WinRateStandardErrorPct(gradedPicks));

    /// <summary>Expectancy gap that opens a warning: two standard errors, floored.</summary>
    public static decimal RequiredExpectancyGapR(int gradedPicks) => gradedPicks <= 0
        ? MinimumExpectancyGapR
        : Math.Max(
            MinimumExpectancyGapR,
            2m * ExpectancyStandardDeviationR / (decimal)Math.Sqrt(gradedPicks));

    /// <summary>
    /// Risk multiplier for a status. Applied <b>after</b> every other governor multiplier, so the
    /// pod-shop clamp on those (0.50x to 1.50x) cannot floor a 0.35x or a 0.00x back up.
    /// </summary>
    public static decimal MultiplierFor(LiveDriftStatus status) => status switch
    {
        LiveDriftStatus.HALTED => HaltedMultiplier,
        LiveDriftStatus.DEGRADED => DegradedMultiplier,
        LiveDriftStatus.WARNING => WarningMultiplier,
        _ => StableMultiplier
    };

    /// <summary>
    /// Classifies the drift of one horizon book. <paramref name="realizedWinRate"/> and
    /// <paramref name="expectedWinRate"/> are <b>fractions</b> in <c>[0, 1]</c> (0.45 means 45%),
    /// while the thresholds are percentage points, so the gap is converted before it is compared;
    /// <paramref name="rollingExpectancyR"/> and <paramref name="baselineExpectancyR"/> are in R.
    /// <paramref name="previous"/> implements the hysteresis: a throttled book only returns to
    /// <see cref="LiveDriftStatus.STABLE"/> once the gap has fallen to half the entry threshold.
    /// </summary>
    public static LiveDriftStatus Classify(
        int gradedPicks,
        decimal realizedWinRate,
        decimal expectedWinRate,
        decimal rollingExpectancyR,
        decimal baselineExpectancyR,
        LiveDriftStatus previous = LiveDriftStatus.STABLE)
    {
        if (gradedPicks < MinimumGradedPicks)
        {
            // Not enough evidence to throttle on. A book that was already halted stays halted
            // until it has enough fresh graded picks to be re-judged.
            return previous == LiveDriftStatus.HALTED ? LiveDriftStatus.HALTED : LiveDriftStatus.STABLE;
        }

        if (rollingExpectancyR <= HaltedExpectancyR)
        {
            return LiveDriftStatus.HALTED;
        }

        if (rollingExpectancyR < DegradedExpectancyR)
        {
            return LiveDriftStatus.DEGRADED;
        }

        var winRateGapPct = (expectedWinRate - realizedWinRate) * 100m;
        var expectancyGap = baselineExpectancyR - rollingExpectancyR;
        var requiredWinRateGap = RequiredWinRateGapPct(gradedPicks);
        var requiredExpectancyGap = RequiredExpectancyGapR(gradedPicks);

        var breach = winRateGapPct >= requiredWinRateGap || expectancyGap >= requiredExpectancyGap;

        if (previous == LiveDriftStatus.STABLE)
        {
            return breach ? LiveDriftStatus.WARNING : LiveDriftStatus.STABLE;
        }

        // Hysteresis: releasing a throttle needs the gap down to half the entry threshold, so a
        // book cannot flap between throttled and full size on a single lucky week.
        var released = winRateGapPct < (requiredWinRateGap / 2m) &&
                       expectancyGap < (requiredExpectancyGap / 2m);

        return released ? LiveDriftStatus.STABLE : LiveDriftStatus.WARNING;
    }

    /// <summary>Human-readable reason for a throttle, for the ticket warnings and the UI.</summary>
    public static string? Describe(
        LiveDriftStatus status,
        int gradedPicks,
        decimal winRateGapPct,
        decimal expectancyGapR) =>
        status switch
        {
            LiveDriftStatus.HALTED =>
                $"Live drift HALTED: {gradedPicks} graded picks are {expectancyGapR:0.##}R below the model's " +
                "promoted expectancy and the rolling expectancy is negative. No new risk is sized.",
            LiveDriftStatus.DEGRADED =>
                $"Live drift DEGRADED: the rolling expectancy of the last {gradedPicks} graded picks is no longer " +
                $"positive ({expectancyGapR:0.##}R below the promoted record). New risk is cut to 0.35x.",
            LiveDriftStatus.WARNING =>
                $"Live drift WARNING: {gradedPicks} graded picks are {winRateGapPct:0.#}pp / {expectancyGapR:0.##}R " +
                "behind the model's promoted record. New risk is cut to 0.65x until it recovers.",
            _ => null
        };
}
