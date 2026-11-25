using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The market call is the first thing a beginner reads, so these tests pin the promises it
/// makes: five deterministic pillars around a neutral 50, a hard cash override on a bearish
/// regime, a "nothing cleared the hurdle" step-down when the list is empty, descriptive
/// rather than imperative copy, and an honest treatment of unreported dials (never a
/// fabricated 0% or PHP 0).
/// </summary>
public sealed class MarketDirectionServiceTests
{
    [Fact]
    public void Evaluate_clamps_a_relentless_bull_tape_to_90_and_calls_ride_the_wave()
    {
        var snapshot = BuildBullishSnapshot();
        var run = BuildRun(EdgeVerdict.TRADE, EdgeVerdict.SMALL_SIZE, EdgeVerdict.WATCH_ONLY);

        var forecast = MarketDirectionService.Evaluate(snapshot, run);

        Assert.Equal(MarketDirectionService.RideTheWave, forecast.Call);
        Assert.Equal(MarketDirectionService.UpTrend, forecast.Direction);
        Assert.Equal(90, forecast.BullishScore);
        Assert.Equal(MarketDirectionService.HighConfidence, forecast.Confidence);
        Assert.Contains("Ride the wave", forecast.Headline, StringComparison.Ordinal);
        Assert.Contains("20%", forecast.ActionSummary, StringComparison.Ordinal);
        Assert.Contains("2 setups", forecast.ActionSummary, StringComparison.Ordinal);
        Assert.InRange(forecast.Reasons.Count, 2, 4);
        Assert.All(forecast.Reasons, reason => Assert.False(string.IsNullOrWhiteSpace(reason)));
    }

    [Fact]
    public void Evaluate_forces_cash_on_a_bearish_regime_even_with_bullish_dials()
    {
        var snapshot = BuildBullishSnapshot();
        snapshot.MarketRegime = MarketRegime.BEARISH;

        var forecast = MarketDirectionService.Evaluate(snapshot);

        Assert.Equal(MarketDirectionService.StayInCash, forecast.Call);
        Assert.Contains(
            forecast.Reasons,
            reason => reason.Contains("bearish", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("cash", forecast.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("80%", forecast.ActionSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_steps_ride_the_wave_down_to_be_selective_when_nothing_is_actionable()
    {
        var snapshot = BuildBullishSnapshot();
        var run = BuildRun(EdgeVerdict.WATCH_ONLY, EdgeVerdict.WATCH_ONLY);

        var forecast = MarketDirectionService.Evaluate(snapshot, run);

        Assert.Equal(MarketDirectionService.BeSelective, forecast.Call);
        Assert.Contains(
            forecast.Reasons,
            reason => reason.Contains("No pick cleared its fees", StringComparison.Ordinal));
        Assert.Contains("cleared the hurdle", forecast.Headline, StringComparison.Ordinal);
        Assert.Contains("list stays empty", forecast.ActionSummary, StringComparison.Ordinal);
        // The copy is descriptive: it must never tell the reader to place an order.
        Assert.DoesNotContain("you ", forecast.Headline + " " + forecast.ActionSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_never_prints_a_zero_for_an_unreported_dial()
    {
        // Only the index trend reports: no breadth, no rotation, no foreign flow, no FX/vol.
        var snapshot = new DailyMarketSnapshot
        {
            TradeDate = new DateOnly(2026, 11, 24),
            PseiClose = 8_500m,
            PseiSma50 = 8_400m,
            PseiSma200 = 0m,
            MarketRegime = MarketRegime.NEUTRAL,
            RegimeNote = "Thin tape.",
            SectorRotationJson = "[]"
        };

        var forecast = MarketDirectionService.Evaluate(snapshot);

        Assert.Equal(58, forecast.BullishScore);
        Assert.Equal(MarketDirectionService.Sideways, forecast.Direction);
        Assert.Equal(MarketDirectionService.BeSelective, forecast.Call);
        Assert.Equal(MarketDirectionService.CautiousConfidence, forecast.Confidence);
        Assert.Empty(forecast.LeadingSectors);
        Assert.Empty(forecast.LaggingSectors);
        Assert.InRange(forecast.Reasons.Count, 2, 4);
        Assert.DoesNotContain(forecast.Reasons, reason => reason.Contains("0%", StringComparison.Ordinal));
        Assert.DoesNotContain(forecast.Reasons, reason => reason.Contains("PHP 0", StringComparison.Ordinal));
        Assert.Contains(
            forecast.Reasons,
            reason => reason.Contains("unreported against", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_names_the_sectors_money_is_moving_into_and_out_of()
    {
        var snapshot = BuildBullishSnapshot();
        snapshot.SectorRotationJson = MarketDataMapper.SerializeSectorRotation(new[]
        {
            new SectorRotationDto("Banks", 5, 4.2m, 80m, 60m, 1.4m, 90m, SectorQuadrant.Leading),
            new SectorRotationDto("Property", 4, 2.1m, 65m, 50m, 1.2m, 70m, SectorQuadrant.Improving),
            new SectorRotationDto("Telcos", 3, -1.2m, 30m, 25m, 0.8m, 35m, SectorQuadrant.Weakening),
            new SectorRotationDto("Mining", 2, -2.8m, 20m, 10m, 0.7m, 20m, SectorQuadrant.Lagging)
        });

        var forecast = MarketDirectionService.Evaluate(snapshot);

        Assert.Equal(new[] { "Banks", "Property" }, forecast.LeadingSectors.Take(2));
        Assert.Equal(new[] { "Mining", "Telcos" }, forecast.LaggingSectors.Take(2));
        Assert.Contains(
            forecast.Reasons,
            reason => reason.Contains("money is moving into Banks and Property", StringComparison.Ordinal));
    }

    [Fact]
    public void ToTrustPulse_only_marks_the_record_calibrated_after_twenty_graded_picks()
    {
        var fresh = BuildScorecard(resolved: 0);
        var seasoned = BuildScorecard(resolved: 20);

        var freshPulse = MarketDirectionService.ToTrustPulse(fresh);
        var seasonedPulse = MarketDirectionService.ToTrustPulse(seasoned);

        Assert.False(freshPulse.IsCalibrated);
        Assert.Equal(0, freshPulse.ResolvedCount);
        Assert.True(seasonedPulse.IsCalibrated);
        Assert.Equal(20, seasonedPulse.ResolvedCount);
        Assert.Equal("WEEK", seasonedPulse.Horizon);
    }


    private static DailyMarketSnapshot BuildBullishSnapshot() => new()
    {
        TradeDate = new DateOnly(2026, 11, 24),
        PseiClose = 8_514.08m,
        PseiSma50 = 8_409.71m,
        PseiSma200 = 8_101.25m,
        NetForeignFlowPhp = 1_280_000_000m,
        UsdPhp = 58.27m,
        MarketRegime = MarketRegime.BULLISH,
        RegimeNote = "Risk-on tape.",
        BreadthAboveSma50Pct = 65m,
        BreadthAboveSma200Pct = 48m,
        Stage2Count = 40,
        EvaluatedStockCount = 100,
        UsdPhpChange20dPct = -0.5m,
        PseiRealizedVol20dPct = 11m,
        SectorRotationJson = MarketDataMapper.SerializeSectorRotation(new[]
        {
            new SectorRotationDto("Banks", 5, 4.2m, 80m, 60m, 1.4m, 90m, SectorQuadrant.Leading),
            new SectorRotationDto("Property", 4, 2.1m, 65m, 50m, 1.2m, 70m, SectorQuadrant.Improving),
            new SectorRotationDto("Industrial", 3, 0.4m, 55m, 40m, 1.0m, 55m, SectorQuadrant.Improving),
            new SectorRotationDto("Mining", 2, -2.8m, 20m, 10m, 0.7m, 20m, SectorQuadrant.Lagging)
        })
    };

    private static SignalRun BuildRun(params EdgeVerdict[] verdicts)
    {
        var run = new SignalRun
        {
            RunDate = new DateOnly(2026, 11, 24),
            Horizon = SignalHorizon.WEEK,
            RecommendedCashPct = 20m
        };

        for (var index = 0; index < verdicts.Length; index++)
        {
            run.Items.Add(new SignalItem
            {
                Rank = index + 1,
                Ticker = $"AAA{index}",
                EdgeVerdict = verdicts[index]
            });
        }

        return run;
    }

    private static SignalScorecardDto BuildScorecard(int resolved) => new(
        "WEEK",
        TotalPicks: resolved + 3,
        ResolvedCount: resolved,
        OpenCount: 3,
        WinRate: 0.55m,
        AverageRealizedNetR: 0.4m,
        ExpectancyR: 0.22m,
        ProfitFactor: 1.6m,
        BrierScore: 0.22m,
        Calibration: Array.Empty<CalibrationBucketDto>(),
        Verdicts: Array.Empty<VerdictBreakdownDto>(),
        RecentResolved: Array.Empty<ResolvedSignalDto>());
}

