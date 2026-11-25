using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The wave plan is the only place a beginner is told where to buy, where they are wrong and
/// when to lock a gain. These tests pin what must never drift: every level is a tradeable PSE
/// tick, the four steps are strictly ordered (buy &lt; break-even &lt; +1R &lt; +1.5R &le; target),
/// the chase cap still clears the fee-adjusted edge, and the holding check uses the same
/// fee-paid ladder as the blotter (stop, target, break-even at +1R, EMA20 at +1.5R).
/// </summary>
public sealed class WaveTradePlanCalculatorTests
{
    [Fact]
    public void BuildPlan_lays_out_four_tick_valid_steps_in_order()
    {
        var plan = WaveTradePlanCalculator.BuildPlan(
            entryPrice: 100m,
            stopLoss: 92.5m,
            targetPrice: 115m,
            currentPrice: 100.5m,
            ema20: 101.4m,
            EdgeVerdict.TRADE,
            SignalHorizon.WEEK);

        Assert.Equal(100m, plan.IdealBuyPrice);
        Assert.True(plan.IdealBuyPrice < plan.BreakEvenStopPrice);
        Assert.True(plan.BreakEvenStopPrice <= plan.LockBreakEvenTriggerPrice);
        Assert.True(plan.LockBreakEvenTriggerPrice < plan.TrailWaveTriggerPrice);
        Assert.True(plan.TrailWaveTriggerPrice <= plan.TargetPrice);
        Assert.InRange(plan.MaxChasePrice, plan.IdealBuyPrice, plan.TargetPrice);
        Assert.Equal(101.4m, plan.TrailStopPrice!.Value);
        Assert.Equal(WaveTradePlanCalculator.ReadyNow, plan.EntryStatus);
        Assert.Equal(10, plan.MaxHoldTradingDays);

        foreach (var price in new[]
                 {
                     plan.IdealBuyPrice,
                     plan.CutLossPrice,
                     plan.BreakEvenStopPrice,
                     plan.TargetPrice,
                     plan.MaxChasePrice,
                     plan.LockBreakEvenTriggerPrice,
                     plan.TrailWaveTriggerPrice,
                     plan.TrailStopPrice!.Value
                 })
        {
            Assert.Equal(PseBoardLotService.RoundToValidTick(price), price);
        }
    }

    [Fact]
    public void BuildPlan_never_chases_past_the_price_that_still_pays_its_fees()
    {
        var plan = WaveTradePlanCalculator.BuildPlan(
            100m, 92.5m, 115m, 100m, ema20: null, EdgeVerdict.TRADE, SignalHorizon.WEEK);

        Assert.True(plan.MaxChasePrice > plan.IdealBuyPrice);
        Assert.True(plan.MaxChasePrice <= 103m);

        var netAtCap = PseBoardLotService.ComputeNetRiskReward(
            plan.MaxChasePrice, plan.CutLossPrice, plan.TargetPrice);
        Assert.True(
            netAtCap >= WaveTradePlanCalculator.MinNetRiskReward,
            $"The chase cap must still clear {WaveTradePlanCalculator.MinNetRiskReward}R net, was {netAtCap}R.");
    }

    [Fact]
    public void BuildPlan_returns_the_ideal_entry_when_no_chase_keeps_an_edge()
    {
        var plan = WaveTradePlanCalculator.BuildPlan(
            100m, 99.9m, 100.2m, 100m, ema20: null, EdgeVerdict.TRADE, SignalHorizon.WEEK);

        Assert.Equal(plan.IdealBuyPrice, plan.MaxChasePrice);
    }

    [Fact]
    public void BuildPlan_keeps_a_day_plan_inside_the_exchange_static_ceiling()
    {
        var ceiling = PseBoardLotService.GetStaticPriceLimits(10m).StaticCeiling;
        var plan = WaveTradePlanCalculator.BuildPlan(
            entryPrice: 14m,
            stopLoss: 13m,
            targetPrice: 20m,
            currentPrice: 10m,
            ema20: null,
            EdgeVerdict.TRADE,
            SignalHorizon.DAY);

        Assert.True(plan.MaxChasePrice <= ceiling);
        Assert.True(plan.MaxChasePrice > plan.IdealBuyPrice);
        Assert.Equal(3, plan.MaxHoldTradingDays);
    }

    [Fact]
    public void ComputeTriggerPrice_lands_exactly_on_the_fee_paid_one_r_tick()
    {
        var lockPrice = WaveTradePlanCalculator.ComputeTriggerPrice(100m, 92.5m, 115m, 1m);
        var netRisk = PseBoardLotService.ComputeNetRiskPerShare(100m, 92.5m);
        var gainAtLock = PseBoardLotService.ComputeNetPerSharePnl(100m, lockPrice);
        var oneTickBelow = lockPrice - PseBoardLotService.GetTickSize(lockPrice);
        var gainBelow = PseBoardLotService.ComputeNetPerSharePnl(100m, oneTickBelow);

        Assert.True(
            gainAtLock >= netRisk,
            $"At PHP {lockPrice} the fee-paid gain is {gainAtLock}, which must cover the {netRisk} risk.");
        Assert.True(
            gainBelow < netRisk,
            $"One tick below PHP {lockPrice} the gain is {gainBelow}; the trigger must be the first tick that pays 1R.");
        Assert.True(lockPrice <= 115m);
    }

    [Fact]
    public void BuildPlan_reads_the_entry_status_from_the_mark()
    {
        Assert.Equal(
            WaveTradePlanCalculator.ReadyNow,
            PlanAt(100.5m).EntryStatus);
        Assert.Equal(
            WaveTradePlanCalculator.NearEntry,
            PlanAt(95m).EntryStatus);
        Assert.Equal(
            WaveTradePlanCalculator.ExtendedWaitPullback,
            PlanAt(106m).EntryStatus);
        Assert.Equal(
            WaveTradePlanCalculator.BrokenBelowStop,
            PlanAt(91m).EntryStatus);

        var noMark = WaveTradePlanCalculator.BuildPlan(
            100m, 92.5m, 115m, 0m, ema20: null, EdgeVerdict.TRADE, SignalHorizon.WEEK);
        Assert.Equal(WaveTradePlanCalculator.WaitWatchOnly, noMark.EntryStatus);

        var watchOnly = WaveTradePlanCalculator.BuildPlan(
            100m, 92.5m, 115m, 100m, ema20: null, EdgeVerdict.WATCH_ONLY, SignalHorizon.WEEK);
        Assert.Equal(WaveTradePlanCalculator.WaitWatchOnly, watchOnly.EntryStatus);
    }

    [Fact]
    public void EvaluateHolding_holds_below_one_r_and_reports_the_fee_paid_peso_result()
    {
        var advice = WaveTradePlanCalculator.EvaluateHolding(
            "ICT", 100m, 103m, 1_000, 5m, 101.4m, horizon: SignalHorizon.WEEK);

        Assert.Equal("HOLD", advice.Action);
        Assert.Equal(92.5m, advice.SuggestedStopPrice);
        Assert.Equal(100.70m, advice.BreakEvenPrice);
        Assert.Equal(115m, advice.SuggestedTargetPrice);
        // 2.29815 pesos a share after both legs, i.e. 2.30% of the 100 average cost. The
        // rounded peso figure below (2,298.15) is the same number at 1,000 shares, so the two
        // agree to the centavo instead of drifting by a rounding artefact.
        Assert.Equal(2.30m, advice.NetReturnPct);
        Assert.Equal(2_298.15m, advice.UnrealizedNetPnlPhp!.Value);
        Assert.Equal(0.28m, advice.UnrealizedR);
        Assert.Equal(500, advice.RecommendedTrimShares);
        Assert.Contains("Plan unchanged", advice.Headline, StringComparison.Ordinal);
        Assert.Contains("PHP 92.50", advice.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateHolding_locks_break_even_at_one_r()
    {
        var advice = WaveTradePlanCalculator.EvaluateHolding(
            "ICT", 100m, 110.2m, 1_000, 5m, 109m, horizon: SignalHorizon.WEEK);

        Assert.Equal("MOVE_STOP_TO_BREAKEVEN", advice.Action);
        Assert.Equal(100.70m, advice.SuggestedStopPrice);
        Assert.Contains("Break-even stop", advice.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PHP 100.70", advice.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void EvaluateHolding_trails_ema20_at_one_and_a_half_r()
    {
        // A huge ATR collapses the normalised stop to one tick, so the +1.5R leg is reached
        // long before the far ATR target — the only honest way to exercise the trail branch.
        var advice = WaveTradePlanCalculator.EvaluateHolding(
            "ICT", 100m, 110m, 1_000, 80m, 108m, horizon: SignalHorizon.WEEK);

        Assert.Equal("TRAIL_STOP_EMA20", advice.Action);
        Assert.Equal(108m, advice.SuggestedStopPrice);
        Assert.Contains("trail the stop", advice.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EvaluateHolding_puts_a_breached_stop_and_a_reached_target_first()
    {
        var stopped = WaveTradePlanCalculator.EvaluateHolding(
            "ICT", 100m, 91m, 1_000, 5m, 95m, horizon: SignalHorizon.WEEK);
        var target = WaveTradePlanCalculator.EvaluateHolding(
            "ICT", 100m, 115m, 1_000, 5m, 112m, horizon: SignalHorizon.WEEK);

        Assert.Equal("STOP_BREACHED", stopped.Action);
        Assert.Contains("Cut-loss level breached", stopped.Headline, StringComparison.Ordinal);
        Assert.True(stopped.NetReturnPct < 0m);

        Assert.Equal("TARGET_REACHED", target.Action);
        Assert.Contains("Target reached", target.Headline, StringComparison.Ordinal);
        Assert.True(target.NetReturnPct > 0m);
    }

    [Fact]
    public void EvaluateHolding_never_reports_a_position_it_was_not_given()
    {
        var advice = WaveTradePlanCalculator.EvaluateHolding(
            "ICT", 100m, 103m, 0, 5m, null, horizon: SignalHorizon.WEEK);

        Assert.Null(advice.UnrealizedNetPnlPhp);
        Assert.Equal(0, advice.RecommendedTrimShares);
        Assert.Equal(0, advice.Shares);
    }

    [Fact]
    public void ComputeRecommendedTrimShares_only_returns_whole_board_lots_on_both_legs()
    {
        Assert.Equal(500, WaveTradePlanCalculator.ComputeRecommendedTrimShares(1_000, 100m, 103m));
        Assert.Equal(0, WaveTradePlanCalculator.ComputeRecommendedTrimShares(15, 100m, 103m));
        Assert.Equal(10, WaveTradePlanCalculator.ComputeRecommendedTrimShares(30, 100m, 103m));
        Assert.Equal(0, WaveTradePlanCalculator.ComputeRecommendedTrimShares(0, 100m, 103m));
    }

    private static WaveTradePlanDto PlanAt(decimal currentPrice) =>
        WaveTradePlanCalculator.BuildPlan(
            100m, 92.5m, 115m, currentPrice, ema20: null, EdgeVerdict.TRADE, SignalHorizon.WEEK);
}

