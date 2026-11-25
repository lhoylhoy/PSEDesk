using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The order ticket is the desk's own sizing arithmetic, so it is tested on its own: whole
/// board lots, all-in buy cash, fee-paid peso risk and reward, and the independent caps
/// (risk budget, heat room, single position, free cash, liquidity).
/// </summary>
public sealed class OrderTicketCalculatorTests
{
    private static readonly OrderTicketContext Capital = new(
        EquityPhp: 1_000_000m,
        CashPhp: 1_000_000m,
        MaxRiskPerTradePct: 1m,
        SmallSizeRiskMultiplier: 0.5m,
        MaxPositionPct: 20m,
        OpenHeatPct: 0m,
        MaxPortfolioHeatPct: 6m);

    [Fact]
    public void Build_sizes_whole_board_lots_from_the_risk_budget()
    {
        var ticket = OrderTicketCalculator.Build(Capital, 100m, 95m, 110m, EdgeVerdict.TRADE, 25_000_000m);

        // PHP 10,000 of risk over PHP 5.67025 of fee-paid risk per share = 1,763 shares,
        // floored to 176 ten-share board lots. The published risk is the *settled* peso figure
        // (all-in buy cash minus the stop-out proceeds, each leg's commission floored at PHP 20
        // and VAT'd), not shares * the per-share estimate, which is why it differs by centavos.
        Assert.Equal(10, ticket.BoardLotSize);
        Assert.Equal(176, ticket.Lots);
        Assert.Equal(1_760, ticket.Shares);
        Assert.Equal(10_000m, ticket.RiskBudgetPhp);
        Assert.Equal(PseBoardLotService.ComputeBuyTotalCostPhp(1_760, 100m), ticket.BuyOutlayPhp);
        Assert.Equal(PseBoardLotService.ComputeNetRiskPhp(1_760, 100m, 95m), ticket.NetRiskPhp);
        Assert.Equal(9_979.64m, ticket.NetRiskPhp);
        Assert.Equal(16_316.08m, ticket.NetRewardPhp);
        Assert.Equal(PseBoardLotService.ComputeBreakEvenStopPrice(100m), ticket.BreakEvenStopPrice);
        Assert.False(ticket.LiquidityCapped);
    }

    [Fact]
    public void Build_halves_the_risk_budget_for_a_small_size_verdict()
    {
        var ticket = OrderTicketCalculator.Build(Capital, 100m, 95m, 110m, EdgeVerdict.SMALL_SIZE, 25_000_000m);

        Assert.Equal(5_000m, ticket.RiskBudgetPhp);
        Assert.Equal(88, ticket.Lots);
        Assert.Equal(880, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("SMALL_SIZE", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_prices_a_watch_only_study_with_zero_shares()
    {
        var ticket = OrderTicketCalculator.Build(Capital, 100m, 95m, 110m, EdgeVerdict.WATCH_ONLY, 25_000_000m);

        Assert.Equal(0m, ticket.RiskBudgetPhp);
        Assert.Equal(0, ticket.Shares);
        Assert.Equal(0, ticket.Lots);
        Assert.Equal(10, ticket.BoardLotSize);
        Assert.True(ticket.BreakEvenStopPrice > 100m);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("WATCH_ONLY", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_cuts_the_budget_to_the_remaining_portfolio_heat()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { OpenHeatPct = 5.5m, MaxPortfolioHeatPct = 6m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // Only 0.5% of equity is left before the 6% heat cap, so PHP 5,000, not PHP 10,000.
        Assert.Equal(5_000m, ticket.RiskBudgetPhp);
        Assert.Equal(880, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("Portfolio heat", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_refuses_new_risk_once_the_heat_cap_is_reached()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { OpenHeatPct = 6m, MaxPortfolioHeatPct = 6m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(0m, ticket.RiskBudgetPhp);
        Assert.Equal(0, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("exhausted", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_caps_the_order_at_the_free_cash()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { CashPhp = 50_000m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // PHP 50,000 / PHP 100.295 all-in per share = 498 shares, floored to 49 lots.
        Assert.Equal(49, ticket.Lots);
        Assert.Equal(490, ticket.Shares);
        Assert.True(ticket.BuyOutlayPhp <= 50_000m);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("Free cash", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_caps_the_order_at_the_single_position_limit()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { MaxPositionPct = 5m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // 5% of PHP 1,000,000 is PHP 50,000, which funds 498 shares (49 lots).
        Assert.Equal(490, ticket.Shares);
        Assert.True(ticket.BuyOutlayPhp <= 50_000m);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("single-position cap", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_caps_the_order_at_two_percent_of_the_twenty_day_turnover()
    {
        var ticket = OrderTicketCalculator.Build(Capital, 100m, 95m, 110m, EdgeVerdict.TRADE, 1_000_000m);

        // 2% of PHP 1,000,000 is PHP 20,000, i.e. 200 shares — 20 board lots.
        Assert.True(ticket.LiquidityCapped);
        Assert.Equal(20, ticket.Lots);
        Assert.Equal(200, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("20-day turnover", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_skips_the_liquidity_cap_when_no_turnover_is_reported()
    {
        var ticket = OrderTicketCalculator.Build(Capital, 100m, 95m, 110m, EdgeVerdict.TRADE, 0m);

        Assert.False(ticket.LiquidityCapped);
        Assert.Equal(1_760, ticket.Shares);
    }

    [Fact]
    public void Build_returns_a_zero_share_study_when_the_stop_carries_no_risk()
    {
        var ticket = OrderTicketCalculator.Build(Capital, 100m, 100m, 110m, EdgeVerdict.TRADE, 25_000_000m);

        Assert.Equal(0, ticket.Shares);
        Assert.Equal(0m, ticket.NetRiskPhp);
        Assert.Equal(0m, ticket.NetRewardPhp);
        Assert.True(ticket.BreakEvenStopPrice > 100m);
    }

    [Fact]
    public void Build_scales_the_risk_to_the_edge_kelly_stake()
    {
        // A 2% quarter-Kelly stake is the 1.0x reference; a 3% stake is capped at 1.5x.
        var reference = OrderTicketCalculator.Build(
            Capital,
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m,
            new EdgeAssessment(2m, 0.6m, 0.33m, 0.8m, 0.02m, EdgeVerdict.TRADE));

        Assert.Equal(1m, reference.EdgeMultiplier);
        Assert.Equal(1m, reference.EffectiveRiskPct);

        var strong = OrderTicketCalculator.Build(
            Capital,
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m,
            new EdgeAssessment(2m, 0.62m, 0.33m, 1.2m, 0.06m, EdgeVerdict.TRADE));

        // 1% * 1.5 = 1.5% of PHP 1,000,000 = PHP 15,000, which the 20% position cap trims to
        // 1,990 shares (199 board lots).
        Assert.Equal(1.5m, strong.EdgeMultiplier);
        Assert.Equal(1.5m, strong.EffectiveRiskPct);
        Assert.Equal(15_000m, strong.RiskBudgetPhp);
        Assert.Equal(1_990, strong.Shares);
        Assert.Contains(strong.Warnings, warning => warning.Contains("single-position cap", StringComparison.Ordinal));

        var weak = OrderTicketCalculator.Build(
            Capital,
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m,
            new EdgeAssessment(2m, 0.5m, 0.33m, 0.1m, 0.01m, EdgeVerdict.TRADE));

        Assert.Equal(0.5m, weak.EdgeMultiplier);
        Assert.Equal(0.5m, weak.EffectiveRiskPct);
        Assert.Equal(880, weak.Shares);
    }

    [Fact]
    public void Build_throttles_risk_between_the_soft_and_hard_drawdown_limits()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { DrawdownPct = 7.5m, SoftDrawdownThresholdPct = 5m, HardDrawdownThresholdPct = 10m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // Halfway through the 5%-10% band the multiplier is 1.0 - 0.5 * 0.5 = 0.75x.
        Assert.Equal(RiskGovernorState.Defensive, ticket.RiskGovernor);
        Assert.Equal(0.75m, ticket.DrawdownMultiplier);
        Assert.Equal(0.75m, ticket.EffectiveRiskPct);
        Assert.Equal(7.5m, ticket.DrawdownPct);
        Assert.Equal(1_320, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("soft limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_halts_new_risk_past_the_hard_drawdown_limit()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { DrawdownPct = 12m, SoftDrawdownThresholdPct = 5m, HardDrawdownThresholdPct = 10m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Halted, ticket.RiskGovernor);
        Assert.Equal(0m, ticket.DrawdownMultiplier);
        Assert.Equal(0, ticket.Shares);
        Assert.Equal(0m, ticket.RiskBudgetPhp);
        Assert.Equal(ExecutionGuidance.NoTrade, ticket.ExecutionGuidance);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("hard limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_targets_volatility_against_the_realized_pse_index_volatility()
    {
        var calm = OrderTicketCalculator.Build(
            Capital with { RegimeVolPct = 12m, TargetAnnualVolPct = 18m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // A calm tape never scales risk up: the multiplier is clamped at 1.0x.
        Assert.Equal(1m, calm.VolatilityMultiplier);

        var stressed = OrderTicketCalculator.Build(
            Capital with { RegimeVolPct = 36m, TargetAnnualVolPct = 18m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(0.5m, stressed.VolatilityMultiplier);
        Assert.Equal(0.5m, stressed.EffectiveRiskPct);
        Assert.Equal(880, stressed.Shares);
        Assert.Contains(stressed.Warnings, warning => warning.Contains("Realised PSEi volatility", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_refuses_to_average_down_an_open_position()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { ExistingEntryPrice = 105m, Sector = "Banks" },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Blocked, ticket.RiskGovernor);
        Assert.Equal(0, ticket.Shares);
        Assert.Equal(ExecutionGuidance.NoTrade, ticket.ExecutionGuidance);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("No averaging down", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_allows_a_higher_entry_once_the_open_winner_is_at_break_even_and_one_r()
    {
        // Adding on strength is legal only in the pyramid shape: above the open entry, the stop
        // already at break-even, at least +1R in the trade and at most two adds.
        var ticket = OrderTicketCalculator.Build(
            Capital with
            {
                ExistingEntryPrice = 95m,
                ExistingStopAtBreakEven = true,
                ExistingUnrealizedR = 1.2m
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Normal, ticket.RiskGovernor);
        // The first add is half a clip: PHP 5,000 of risk, floored to 88 board lots.
        Assert.Equal(0.5m, ticket.EffectiveRiskPct);
        Assert.Equal(5_000m, ticket.RiskBudgetPhp);
        Assert.Equal(880, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("Pyramid add 1 of 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_blocks_a_pyramid_add_before_the_stop_reaches_break_even()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with
            {
                ExistingEntryPrice = 95m,
                ExistingStopAtBreakEven = false,
                ExistingUnrealizedR = 2.5m
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Blocked, ticket.RiskGovernor);
        Assert.Equal(0, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("break-even tick", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_blocks_a_pyramid_add_below_one_r()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with
            {
                ExistingEntryPrice = 95m,
                ExistingStopAtBreakEven = true,
                ExistingUnrealizedR = 0.8m
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Blocked, ticket.RiskGovernor);
        Assert.Equal(0, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("+1R unrealized profit", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_blocks_a_third_pyramid_add()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with
            {
                ExistingEntryPrice = 95m,
                ExistingStopAtBreakEven = true,
                ExistingUnrealizedR = 3m,
                ExistingPyramidCount = OrderTicketCalculator.MaxPyramidAdds
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Blocked, ticket.RiskGovernor);
        Assert.Equal(0, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("pyramid adds reached", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_sizes_the_second_pyramid_add_at_a_quarter_clip()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with
            {
                ExistingEntryPrice = 95m,
                ExistingStopAtBreakEven = true,
                ExistingUnrealizedR = 2.0m,
                ExistingPyramidCount = 1
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // The second add is a quarter clip: PHP 2,500 of risk, floored to 44 board lots.
        Assert.Equal(0.25m, ticket.EffectiveRiskPct);
        Assert.Equal(2_500m, ticket.RiskBudgetPhp);
        Assert.Equal(440, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("Pyramid add 2 of 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_exempts_a_same_ticker_add_from_the_sector_name_cap()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with
            {
                Sector = "Banks",
                SectorPositionCount = 2,
                MaxPositionsPerSector = 2,
                ExistingEntryPrice = 95m,
                ExistingStopAtBreakEven = true,
                ExistingUnrealizedR = 1.5m
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // The sector already holds its two names, but this clip is one of those names, so only
        // the exposure, position, cash and heat caps still apply.
        Assert.Equal(RiskGovernorState.Normal, ticket.RiskGovernor);
        Assert.Equal(880, ticket.Shares);
        Assert.DoesNotContain(
            ticket.Warnings,
            warning => warning.Contains("positions are already open", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_caps_a_pyramid_add_to_the_room_left_in_the_position_cap()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with
            {
                ExistingEntryPrice = 95m,
                ExistingStopAtBreakEven = true,
                ExistingUnrealizedR = 1.5m,
                ExistingPositionCostPhp = 180_000m
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // PHP 200,000 of position room minus PHP 180,000 already committed leaves PHP 20,000,
        // which funds 190 shares (19 board lots).
        Assert.Equal(190, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("single-position cap", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_blocks_a_sector_that_already_holds_the_name_limit()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { Sector = "Banks", SectorPositionCount = 2, MaxPositionsPerSector = 2 },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Blocked, ticket.RiskGovernor);
        Assert.Equal(0, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("Banks positions are already open", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_caps_the_order_to_the_room_left_in_the_sector_cap()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { Sector = "Banks", SectorExposurePct = 34m, MaxSectorExposurePct = 35m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // 1% of PHP 1,000,000 of room buys 99 shares, floored to 9 board lots.
        Assert.Equal(RiskGovernorState.Normal, ticket.RiskGovernor);
        Assert.Equal(90, ticket.Shares);
        Assert.Equal(9, ticket.Lots);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("sector cap leaves PHP 10000", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_blocks_a_sector_whose_cap_is_already_fully_used()
    {
        var ticket = OrderTicketCalculator.Build(
            Capital with { Sector = "Banks", SectorExposurePct = 35m, MaxSectorExposurePct = 35m },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        Assert.Equal(RiskGovernorState.Blocked, ticket.RiskGovernor);
        Assert.Equal(0, ticket.Shares);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("already fully used", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_prices_the_pse_transaction_cost_analysis_on_the_clip()
    {
        var ticket = OrderTicketCalculator.Build(Capital, 100m, 95m, 110m, EdgeVerdict.TRADE, 25_000_000m);

        // At PHP 100 the tick is PHP 0.10, i.e. 0.1% per crossing and 0.2% for the round trip.
        Assert.Equal(0.2m, ticket.SlippageRatePct);
        Assert.True(ticket.EstimatedSlippagePhp > 0m);
        Assert.Equal(ticket.NetRiskPhp + ticket.EstimatedSlippagePhp, ticket.NetRiskAfterSlippagePhp);
        Assert.Equal(ticket.NetRewardPhp - ticket.EstimatedSlippagePhp, ticket.NetRewardAfterSlippagePhp);
        // 1,760 shares at PHP 100 is about 0.7% of a PHP 25m turnover.
        Assert.InRange(ticket.AdvParticipationPct, 0.6m, 0.8m);
        Assert.Equal(ExecutionGuidance.LiquidMarketable, ticket.ExecutionGuidance);
    }

    [Fact]
    public void Build_warns_and_goes_limit_only_on_a_coarse_tick_bracket()
    {
        // A PHP 0.60 name ticks at PHP 0.01, so a round trip crosses ~3.3% of spread.
        var ticket = OrderTicketCalculator.Build(Capital, 0.60m, 0.55m, 0.75m, EdgeVerdict.TRADE, 25_000_000m);

        Assert.True(ticket.SlippageRatePct >= 3m);
        Assert.Equal(ExecutionGuidance.LimitOnly, ticket.ExecutionGuidance);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("slippage", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_steps_down_whole_lots_so_the_minimum_commission_cannot_breach_the_risk_budget()
    {
        // PHP 4.00 trades in 1,000-share board lots, so one lot is only PHP 4,000 of gross per
        // leg: below the PHP 8,000 where 0.25% of the clip stops being larger than the PHP 20
        // minimum commission. With a wide PHP 1.50 stop the per-share estimate funds two lots
        // while the settled risk of two lots (each leg paying the floor, VAT'd) is over budget.
        var smallBook = Capital with
        {
            EquityPhp = 305_000m,
            CashPhp = 305_000m
        };

        var ticket = OrderTicketCalculator.Build(smallBook, 4.00m, 2.50m, 6.00m, EdgeVerdict.TRADE, 25_000_000m);

        Assert.Equal(3_050m, ticket.RiskBudgetPhp);
        Assert.Equal(1_000, ticket.BoardLotSize);
        Assert.Equal(1, ticket.Lots);
        Assert.Equal(1_000, ticket.Shares);
        Assert.Equal(PseBoardLotService.ComputeNetRiskPhp(1_000, 4.00m, 2.50m), ticket.NetRiskPhp);
        Assert.True(
            ticket.NetRiskPhp <= ticket.RiskBudgetPhp,
            $"settled risk {ticket.NetRiskPhp} breached the PHP {ticket.RiskBudgetPhp} budget");
        Assert.Contains(
            ticket.Warnings,
            warning => warning.Contains("minimum commission", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_refuses_a_lot_whose_fee_paid_risk_exceeds_the_whole_budget()
    {
        var tinyBook = Capital with
        {
            EquityPhp = 100_000m,
            CashPhp = 100_000m
        };

        // PHP 1,000 of risk cannot carry a 1,000-share lot of a PHP 4.00 name stopped 37% lower:
        // the settled risk of that single lot is about PHP 1,548.
        var ticket = OrderTicketCalculator.Build(tinyBook, 4.00m, 2.50m, 6.00m, EdgeVerdict.TRADE, 25_000_000m);

        Assert.Equal(0, ticket.Shares);
        Assert.Equal(0, ticket.Lots);
        Assert.Contains(
            ticket.Warnings,
            warning => warning.Contains("cannot carry a 1000-share lot", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_sizes_nothing_once_live_drift_halts_the_model()
    {
        var halted = Capital with
        {
            LiveDrift = LiveDriftStatus.HALTED,
            LiveDriftGradedPicks = 22,
            LiveDriftExpectancyGapR = 0.9m
        };

        var ticket = OrderTicketCalculator.Build(halted, 100m, 95m, 110m, EdgeVerdict.TRADE, 25_000_000m);

        Assert.Equal(0, ticket.Shares);
        Assert.Equal(0m, ticket.RiskBudgetPhp);
        Assert.Contains(ticket.Warnings, warning => warning.Contains("HALTED", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_cuts_the_risk_unit_hard_when_live_drift_warns()
    {
        var full = OrderTicketCalculator.Build(Capital, 100m, 95m, 110m, EdgeVerdict.TRADE, 25_000_000m);
        var warned = OrderTicketCalculator.Build(
            Capital with
            {
                LiveDrift = LiveDriftStatus.WARNING,
                LiveDriftGradedPicks = 20,
                LiveDriftWinRateGapPct = 24m,
                LiveDriftExpectancyGapR = 0.7m
            },
            100m,
            95m,
            110m,
            EdgeVerdict.TRADE,
            25_000_000m);

        // 0.65x of the same budget, and the drift throttle is applied outside the governor clamp
        // so its 0.65x is not rounded back up to 1.00x.
        Assert.Equal(decimal.Round(Capital.EquityPhp * 0.01m * 0.65m, 2, MidpointRounding.AwayFromZero), warned.RiskBudgetPhp);
        Assert.True(warned.Shares < full.Shares);
        Assert.Contains(warned.Warnings, warning => warning.Contains("WARNING", StringComparison.Ordinal));
    }
}
