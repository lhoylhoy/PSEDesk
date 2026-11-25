using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Tests.Application;

public sealed class ProbabilisticEdgeServiceTests
{
    private readonly ProbabilisticEdgeService _service = new();

    [Fact]
    public void EvaluateSetup_prices_a_fee_adjusted_edge_and_grades_it_trade()
    {
        var assessment = _service.EvaluateSetup(new SetupEdgeRequest(
            EntryPrice: 100m,
            StopLoss: 95m,
            TargetPrice: 115m,
            ConvictionScore: 80m,
            MarketRegime: MarketRegime.BULLISH,
            Profile: HorizonProfile.For(SignalHorizon.WEEK),
            EmpiricalWinRate: 0.50m,
            RelativeStrength63d: 3.4m,
            VolatilityContractionRatio: 0.78m,
            WeinsteinStage: "STAGE_2",
            BreadthAboveSma50Pct: 66m));

        // PHP 15.00 gross on a PHP 5.00 stop is 3:1 gross but only 2.51:1 after the 0.690% round trip.
        Assert.Equal(2.51m, assessment.NetRiskReward);
        Assert.True(assessment.BreakEvenWinRate > 0.28m && assessment.BreakEvenWinRate < 0.29m);
        Assert.InRange(assessment.WinProbability, 0.50m, 0.56m);
        Assert.True(assessment.ExpectedValueR > 0.40m);
        Assert.True(assessment.QuarterKellyFraction > 0m);
        Assert.Equal(EdgeVerdict.TRADE, assessment.Verdict);
    }

    [Fact]
    public void EvaluateSetup_refuses_a_target_that_cannot_pay_the_round_trip_fees()
    {
        var assessment = _service.EvaluateSetup(new SetupEdgeRequest(
            EntryPrice: 100m,
            StopLoss: 99m,
            TargetPrice: 100.5m,
            ConvictionScore: 90m,
            MarketRegime: MarketRegime.BULLISH,
            Profile: HorizonProfile.For(SignalHorizon.DAY)));

        Assert.Equal(0m, assessment.NetRiskReward);
        Assert.Equal(0m, assessment.WinProbability);
        Assert.Equal(0m, assessment.ExpectedValueR);
        Assert.Equal(0m, assessment.QuarterKellyFraction);
        Assert.Equal(EdgeVerdict.WATCH_ONLY, assessment.Verdict);
    }

    [Fact]
    public void EvaluateSetup_goes_watch_only_when_the_geometry_leaves_negative_expected_value()
    {
        // A 1.5:0.1 barrier against a bearish tape, weak breadth, lagging strength and a trap
        // is exactly the shape the desk must refuse even though the stop is "close".
        var assessment = _service.EvaluateSetup(new SetupEdgeRequest(
            EntryPrice: 100m,
            StopLoss: 70m,
            TargetPrice: 105m,
            ConvictionScore: 50m,
            MarketRegime: MarketRegime.BEARISH,
            Profile: HorizonProfile.For(SignalHorizon.WEEK),
            EmpiricalWinRate: 0.30m,
            RelativeStrength63d: -4m,
            VolatilityContractionRatio: 1.3m,
            WeinsteinStage: "STAGE_4",
            BreadthAboveSma50Pct: 28m,
            TrapFlags: new[] { PseTrapFlag.SRO_DILUTION }));

        Assert.True(assessment.NetRiskReward < 0.2m);
        Assert.True(assessment.ExpectedValueR <= 0m);
        Assert.Equal(0m, assessment.QuarterKellyFraction);
        Assert.Equal(EdgeVerdict.WATCH_ONLY, assessment.Verdict);
    }

    [Fact]
    public void EvaluateSetup_downgrades_a_positive_edge_with_a_soft_trap_to_small_size()
    {
        var clean = _service.EvaluateSetup(BuildStrongRequest(traps: null));
        var warned = _service.EvaluateSetup(BuildStrongRequest(traps: new[] { PseTrapFlag.EX_DIVIDEND_DROP }));

        Assert.Equal(EdgeVerdict.TRADE, clean.Verdict);
        Assert.True(warned.WinProbability < clean.WinProbability);
        Assert.Equal(EdgeVerdict.SMALL_SIZE, warned.Verdict);
    }

    [Fact]
    public void EvaluateSetup_downgrades_a_positive_edge_in_a_bearish_tape_to_small_size()
    {
        var request = BuildStrongRequest(traps: null) with { MarketRegime = MarketRegime.BEARISH };

        var assessment = _service.EvaluateSetup(request);

        Assert.True(assessment.ExpectedValueR > 0m);
        Assert.Equal(EdgeVerdict.SMALL_SIZE, assessment.Verdict);
    }

    [Fact]
    public void ComputeEmpiricalBaseRate_shrinks_a_perfect_sample_toward_the_prior()
    {
        // 80 sessions of a +1.00/day drift: every trend-aligned trial reaches its 3-ATR target
        // inside the ten-bar window. Twenty trials still cannot claim a 100% hit rate.
        var rate = ProbabilisticEdgeService.ComputeEmpiricalBaseRate(BuildTrendingBars(80));

        Assert.Equal(0.7825m, rate);
    }

    [Fact]
    public void ComputeEmpiricalBaseRate_returns_null_without_enough_history()
    {
        Assert.Null(ProbabilisticEdgeService.ComputeEmpiricalBaseRate(BuildTrendingBars(59)));
        Assert.Null(ProbabilisticEdgeService.ComputeEmpiricalBaseRate(Array.Empty<OhlcvBar>()));
    }

    [Fact]
    public void ComputeRecommendedCashPct_holds_more_cash_in_a_thin_bearish_tape()
    {
        Assert.Equal(100m, ProbabilisticEdgeService.ComputeRecommendedCashPct(
            MarketRegime.BEARISH,
            breadthAboveSma50Pct: 24m,
            tradeCount: 0,
            smallSizeCount: 0));

        Assert.Equal(60m, ProbabilisticEdgeService.ComputeRecommendedCashPct(
            MarketRegime.NEUTRAL,
            breadthAboveSma50Pct: 50m,
            tradeCount: 4,
            smallSizeCount: 2));

        Assert.Equal(22m, ProbabilisticEdgeService.ComputeRecommendedCashPct(
            MarketRegime.BULLISH,
            breadthAboveSma50Pct: 70m,
            tradeCount: 6,
            smallSizeCount: 2));
    }

    [Fact]
    public void EvaluateSetup_adds_the_income_tilt_to_a_high_yield_month_position()
    {
        var withoutDividend = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            Profile = HorizonProfile.For(SignalHorizon.MONTH),
            WeinsteinStage = "STAGE_2"
        });

        var withDividend = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            Profile = HorizonProfile.For(SignalHorizon.MONTH),
            WeinsteinStage = "STAGE_2",
            TrailingDividendYieldPct = 5.2m
        });

        Assert.Equal(0.015m, withDividend.WinProbability - withoutDividend.WinProbability);
    }

    [Fact]
    public void EvaluateSetup_ignores_the_income_tilt_on_a_short_horizon()
    {
        var withoutDividend = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            Profile = HorizonProfile.For(SignalHorizon.WEEK)
        });

        var withDividend = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            Profile = HorizonProfile.For(SignalHorizon.WEEK),
            TrailingDividendYieldPct = 5.2m
        });

        Assert.Equal(withoutDividend.WinProbability, withDividend.WinProbability);
    }

    [Fact]
    public void EvaluateSetup_penalises_a_day_tape_whose_atr_is_barely_a_round_trip()
    {
        var withoutAtr = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            Profile = HorizonProfile.For(SignalHorizon.DAY)
        });

        var withAtr = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            Profile = HorizonProfile.For(SignalHorizon.DAY),
            AtrPct = 1.2m
        });

        Assert.Equal(0.025m, withoutAtr.WinProbability - withAtr.WinProbability);
    }

    [Fact]
    public void ComputeEmpiricalBaseRate_grades_each_horizon_against_its_own_barriers()
    {
        var bars = BuildTrendingBars(80);

        var week = ProbabilisticEdgeService.ComputeEmpiricalBaseRate(bars, HorizonProfile.For(SignalHorizon.WEEK));
        var day = ProbabilisticEdgeService.ComputeEmpiricalBaseRate(bars, HorizonProfile.For(SignalHorizon.DAY));

        Assert.NotNull(week);
        Assert.NotNull(day);
        Assert.NotEqual(week, day);
    }

    [Fact]
    public void ComputeHorizonBaseRates_prices_all_four_books_in_one_pass()
    {
        var bars = BuildTrendingBars(120);

        var rates = ProbabilisticEdgeService.ComputeHorizonBaseRates(bars);

        Assert.Equal(4, rates.Count);

        foreach (var horizon in ProbabilisticEdgeService.AllHorizons)
        {
            Assert.Equal(
                ProbabilisticEdgeService.ComputeEmpiricalBaseRate(bars, HorizonProfile.For(horizon)),
                rates[horizon]);
        }
    }

    [Fact]
    public void ComputeHorizonBaseRates_reports_null_instead_of_a_made_up_number()
    {
        var rates = ProbabilisticEdgeService.ComputeHorizonBaseRates(BuildTrendingBars(20));

        Assert.Equal(4, rates.Count);
        Assert.All(rates.Values, rate => Assert.Null(rate));
    }

    [Fact]
    public void EvaluateSetup_rewards_a_leading_sector_and_punishes_a_lagging_one()
    {
        var neutral = _service.EvaluateSetup(BuildStrongRequest(traps: null));
        var leading = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            SectorQuadrant = SectorQuadrant.Leading
        });
        var lagging = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            SectorQuadrant = SectorQuadrant.Lagging
        });

        Assert.Equal(0.02m, leading.WinProbability - neutral.WinProbability);
        Assert.Equal(0.025m, neutral.WinProbability - lagging.WinProbability);
    }

    [Fact]
    public void EvaluateSetup_rewards_accumulation_and_punishes_distribution()
    {
        var neutral = _service.EvaluateSetup(BuildStrongRequest(traps: null));
        var accumulating = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            UpDownVolumeRatio20d = 1.6m
        });
        var distributing = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            UpDownVolumeRatio20d = 0.6m
        });

        Assert.Equal(0.015m, accumulating.WinProbability - neutral.WinProbability);
        Assert.Equal(0.015m, neutral.WinProbability - distributing.WinProbability);
    }

    [Fact]
    public void EvaluateSetup_charges_a_wide_tick_spread_only_above_half_a_percent()
    {
        var tight = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            TickSpreadPct = 0.2m
        });
        var wide = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            TickSpreadPct = 2.0m
        });

        Assert.Equal(0.01m, tight.WinProbability - wide.WinProbability);
    }

    [Fact]
    public void EvaluateSetup_charges_the_slippage_inclusive_fee_rate_when_supplied()
    {
        var feeOnly = _service.EvaluateSetup(BuildStrongRequest(traps: null));
        var slippageInclusive = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            SlippageInclusiveFeeRate = 0.01095m
        });

        // The 0.690% round trip on its own still prints a 2.51:1 plan; the same plan with two
        // ticks of spread charged is 2.28:1, and the bar it must clear rises with it.
        Assert.Equal(2.51m, feeOnly.NetRiskReward);
        Assert.Equal(2.28m, slippageInclusive.NetRiskReward);
        Assert.Equal(0.3051m, slippageInclusive.BreakEvenWinRate);
        Assert.True(slippageInclusive.BreakEvenWinRate > feeOnly.BreakEvenWinRate);

        // A rate that ignores the mandatory round trip is not a discount, it is a bug: the
        // engine falls back to the real fee floor instead of trusting it.
        var belowFloor = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            SlippageInclusiveFeeRate = 0.001m
        });

        Assert.Equal(feeOnly.NetRiskReward, belowFloor.NetRiskReward);
    }

    [Fact]
    public void EvaluateSetup_clamps_the_walk_forward_calibration_offset()
    {
        var neutral = _service.EvaluateSetup(BuildStrongRequest(traps: null));
        var overconfident = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            CalibrationOffsetPct = 25m
        });
        var pessimistic = _service.EvaluateSetup(BuildStrongRequest(traps: null) with
        {
            CalibrationOffsetPct = -25m
        });

        Assert.Equal(0.05m, overconfident.WinProbability - neutral.WinProbability);
        Assert.Equal(0.05m, neutral.WinProbability - pessimistic.WinProbability);
    }

    [Fact]
    public void EvaluateSetup_tilts_the_odds_with_the_official_foreign_flow()
    {
        var baseline = BuildStrongRequest(traps: null);
        var reported = _service.EvaluateSetup(baseline).WinProbability;

        var accumulation = _service.EvaluateSetup(
            baseline with { NetForeignTurnoverRatio = 0.10m }).WinProbability;
        var distribution = _service.EvaluateSetup(
            baseline with { NetForeignTurnoverRatio = -0.10m }).WinProbability;

        Assert.True(accumulation > reported);
        Assert.True(distribution < reported);
        Assert.Equal(0.03m, decimal.Round(reported - distribution, 4));
    }

    [Fact]
    public void EvaluateSetup_treats_an_unreported_foreign_ratio_exactly_like_a_missing_dial()
    {
        var baseline = BuildStrongRequest(traps: null);

        Assert.Equal(
            _service.EvaluateSetup(baseline).WinProbability,
            _service.EvaluateSetup(baseline with { NetForeignTurnoverRatio = null }).WinProbability);
    }

    [Fact]
    public void EvaluateSetup_penalises_an_income_name_whose_yield_no_longer_clears_the_us_ten_year()
    {
        var income = BuildStrongRequest(traps: null) with
        {
            Profile = HorizonProfile.For(SignalHorizon.MONTH),
            TrailingDividendYieldPct = 5.0m,
            Sector = "REIT"
        };

        var withoutRates = _service.EvaluateSetup(income).WinProbability;
        var belowTreasuries = _service.EvaluateSetup(income with { Us10YClosePct = 6.2m }).WinProbability;
        var farAboveTreasuries = _service.EvaluateSetup(income with { Us10YClosePct = 2.0m }).WinProbability;

        Assert.True(belowTreasuries < withoutRates);
        Assert.True(farAboveTreasuries > withoutRates);
    }

    private static SetupEdgeRequest BuildStrongRequest(IReadOnlyList<PseTrapFlag>? traps) =>
        new(
            EntryPrice: 100m,
            StopLoss: 95m,
            TargetPrice: 115m,
            ConvictionScore: 80m,
            MarketRegime: MarketRegime.BULLISH,
            Profile: HorizonProfile.For(SignalHorizon.WEEK),
            EmpiricalWinRate: 0.50m,
            RelativeStrength63d: 3.4m,
            VolatilityContractionRatio: 0.78m,
            WeinsteinStage: "STAGE_2",
            BreadthAboveSma50Pct: 66m,
            TrapFlags: traps);

    private static IReadOnlyList<OhlcvBar> BuildTrendingBars(int count)
    {
        var bars = new List<OhlcvBar>(count);
        var date = new DateOnly(2026, 1, 5);
        var price = 100m;

        for (var index = 0; index < count; index++)
        {
            var close = price + 1m;

            bars.Add(new OhlcvBar(
                date.AddDays(index),
                Open: price,
                High: close + 0.4m,
                Low: price - 0.4m,
                Close: close,
                Volume: 3_000_000));

            price = close;
        }

        return bars;
    }
}
