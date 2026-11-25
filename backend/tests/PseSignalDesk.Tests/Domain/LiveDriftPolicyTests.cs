using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Domain;

/// <summary>
/// The live drift governor decides how much new risk a promoted model may still take. Its whole
/// point is that the thresholds are measured in standard errors, so these tests pin the noise
/// band: a handful of unlucky picks must not halt a healthy book, and a genuinely dead edge must.
/// </summary>
public sealed class LiveDriftPolicyTests
{
    [Fact]
    public void WinRateStandardError_shrinks_as_the_scorecard_grows()
    {
        Assert.Equal(50m, LiveDriftPolicy.WinRateStandardErrorPct(0));
        Assert.Equal(12.91m, LiveDriftPolicy.WinRateStandardErrorPct(15));
        Assert.Equal(9.13m, LiveDriftPolicy.WinRateStandardErrorPct(30));
        Assert.Equal(5m, LiveDriftPolicy.WinRateStandardErrorPct(100));
    }

    [Fact]
    public void RequiredGaps_are_two_standard_errors_but_never_below_the_practical_floor()
    {
        // 2 x 12.91 = 25.8 points at 15 picks, 2 x 5 = 10 points floored to 20 at 100 picks.
        Assert.Equal(25.82m, LiveDriftPolicy.RequiredWinRateGapPct(15));
        Assert.Equal(20m, LiveDriftPolicy.RequiredWinRateGapPct(100));

        Assert.Equal(0.80m, decimal.Round(LiveDriftPolicy.RequiredExpectancyGapR(14), 2, MidpointRounding.AwayFromZero));
        Assert.Equal(0.60m, LiveDriftPolicy.RequiredExpectancyGapR(40));
    }

    [Fact]
    public void MultiplierFor_steps_from_full_size_to_no_size()
    {
        Assert.Equal(1.00m, LiveDriftPolicy.MultiplierFor(LiveDriftStatus.STABLE));
        Assert.Equal(0.65m, LiveDriftPolicy.MultiplierFor(LiveDriftStatus.WARNING));
        Assert.Equal(0.35m, LiveDriftPolicy.MultiplierFor(LiveDriftStatus.DEGRADED));
        Assert.Equal(0.00m, LiveDriftPolicy.MultiplierFor(LiveDriftStatus.HALTED));
    }

    [Fact]
    public void Classify_stays_stable_inside_the_noise_band_and_without_enough_evidence()
    {
        // Nine graded picks: no verdict is possible however bad they look.
        Assert.Equal(
            LiveDriftStatus.STABLE,
            LiveDriftPolicy.Classify(
                gradedPicks: 9,
                realizedWinRate: 0.10m,
                expectedWinRate: 0.55m,
                rollingExpectancyR: -1.2m,
                baselineExpectancyR: 0.45m));

        // Twenty-five picks ten points under a 50% record is well inside two standard errors.
        Assert.Equal(
            LiveDriftStatus.STABLE,
            LiveDriftPolicy.Classify(
                gradedPicks: 25,
                realizedWinRate: 0.40m,
                expectedWinRate: 0.50m,
                rollingExpectancyR: 0.10m,
                baselineExpectancyR: 0.30m));
    }

    [Fact]
    public void Classify_warns_on_a_two_sigma_shortfall_and_degrades_when_expectancy_turns()
    {
        Assert.Equal(
            LiveDriftStatus.WARNING,
            LiveDriftPolicy.Classify(
                gradedPicks: 30,
                realizedWinRate: 0.25m,
                expectedWinRate: 0.55m,
                rollingExpectancyR: 0.05m,
                baselineExpectancyR: 0.45m));

        Assert.Equal(
            LiveDriftStatus.DEGRADED,
            LiveDriftPolicy.Classify(
                gradedPicks: 30,
                realizedWinRate: 0.30m,
                expectedWinRate: 0.55m,
                rollingExpectancyR: -0.10m,
                baselineExpectancyR: 0.45m));

        Assert.Equal(
            LiveDriftStatus.HALTED,
            LiveDriftPolicy.Classify(
                gradedPicks: 30,
                realizedWinRate: 0.20m,
                expectedWinRate: 0.55m,
                rollingExpectancyR: -0.80m,
                baselineExpectancyR: 0.45m));
    }

    [Fact]
    public void Classify_releases_a_throttle_only_after_the_gap_has_halved()
    {
        // Half of the 20-point floor is 10 points; a 12-point gap keeps the throttle on.
        Assert.Equal(
            LiveDriftStatus.WARNING,
            LiveDriftPolicy.Classify(
                gradedPicks: 60,
                realizedWinRate: 0.42m,
                expectedWinRate: 0.54m,
                rollingExpectancyR: 0.20m,
                baselineExpectancyR: 0.60m,
                previous: LiveDriftStatus.WARNING));

        // An 8-point gap and a 0.2R expectancy gap are inside half the entry threshold: released.
        Assert.Equal(
            LiveDriftStatus.STABLE,
            LiveDriftPolicy.Classify(
                gradedPicks: 60,
                realizedWinRate: 0.46m,
                expectedWinRate: 0.54m,
                rollingExpectancyR: 0.40m,
                baselineExpectancyR: 0.60m,
                previous: LiveDriftStatus.WARNING));
    }

    [Fact]
    public void Classify_keeps_a_halted_book_halted_until_it_has_fresh_evidence()
    {
        Assert.Equal(
            LiveDriftStatus.HALTED,
            LiveDriftPolicy.Classify(
                gradedPicks: 3,
                realizedWinRate: 0m,
                expectedWinRate: 0.55m,
                rollingExpectancyR: 0m,
                baselineExpectancyR: 0.45m,
                previous: LiveDriftStatus.HALTED));
    }

    [Fact]
    public void Describe_explains_every_throttle_and_says_nothing_when_stable()
    {
        Assert.Null(LiveDriftPolicy.Describe(LiveDriftStatus.STABLE, 30, 0m, 0m));

        var halted = LiveDriftPolicy.Describe(LiveDriftStatus.HALTED, 30, 20m, 0.90m);
        Assert.Contains("HALTED", halted, StringComparison.Ordinal);
        Assert.Contains("No new risk", halted, StringComparison.Ordinal);

        var warning = LiveDriftPolicy.Describe(LiveDriftStatus.WARNING, 30, 25.5m, 0.70m);
        Assert.Contains("0.65x", warning, StringComparison.Ordinal);

        var degraded = LiveDriftPolicy.Describe(LiveDriftStatus.DEGRADED, 30, 20m, 0.50m);
        Assert.Contains("0.35x", degraded, StringComparison.Ordinal);
    }
}
