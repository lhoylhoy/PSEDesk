using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Domain;

public sealed class PseBoardLotServiceTests
{
    [Theory]
    [InlineData(0.0049, 0.0001)]
    [InlineData(0.0099, 0.0001)]
    [InlineData(0.01, 0.001)]
    [InlineData(0.049, 0.001)]
    [InlineData(0.05, 0.001)]
    [InlineData(0.249, 0.001)]
    [InlineData(0.25, 0.005)]
    [InlineData(0.495, 0.005)]
    [InlineData(0.5, 0.01)]
    [InlineData(4.99, 0.01)]
    [InlineData(5.0, 0.01)]
    [InlineData(9.99, 0.01)]
    [InlineData(10.0, 0.02)]
    [InlineData(19.98, 0.02)]
    [InlineData(20.0, 0.05)]
    [InlineData(49.95, 0.05)]
    [InlineData(50.0, 0.05)]
    [InlineData(99.95, 0.05)]
    [InlineData(100.0, 0.1)]
    [InlineData(199.9, 0.1)]
    [InlineData(200.0, 0.2)]
    [InlineData(499.8, 0.2)]
    [InlineData(500.0, 0.5)]
    [InlineData(999.5, 0.5)]
    [InlineData(1000.0, 1.0)]
    [InlineData(1999.0, 1.0)]
    [InlineData(2000.0, 2.0)]
    [InlineData(4998.0, 2.0)]
    [InlineData(5000.0, 5.0)]
    public void GetTickSize_matches_the_pse_bracket_table(decimal price, decimal expectedTick)
    {
        Assert.Equal(expectedTick, PseBoardLotService.GetTickSize(price));
    }

    [Theory]
    [InlineData(0.0049, 1_000_000)]
    [InlineData(0.0123, 100_000)]
    [InlineData(0.25, 10_000)]
    [InlineData(1.0, 1_000)]
    [InlineData(7.5, 100)]
    [InlineData(49.95, 100)]
    [InlineData(88.0, 10)]
    [InlineData(750.0, 10)]
    [InlineData(1_500.0, 5)]
    [InlineData(9_999.0, 5)]
    public void GetBoardLotSize_matches_the_pse_board_lot_table(decimal price, int expectedLot)
    {
        Assert.Equal(expectedLot, PseBoardLotService.GetBoardLotSize(price));
    }

    [Theory]
    [InlineData(0.0123, 0.012)]
    [InlineData(0.0176, 0.018)]
    [InlineData(4.995, 5.0)]
    [InlineData(4.994, 4.99)]
    [InlineData(17.751, 17.76)]
    [InlineData(100.02, 100.0)]
    [InlineData(150.05, 150.1)]
    [InlineData(499.95, 500.0)]
    [InlineData(1999.6, 2000.0)]
    public void RoundToValidTick_lands_on_an_orderable_price(decimal raw, decimal expected)
    {
        var rounded = PseBoardLotService.RoundToValidTick(raw);

        Assert.Equal(expected, rounded);
        Assert.Equal(0m, rounded % PseBoardLotService.GetTickSize(rounded));
    }

    [Fact]
    public void RoundToValidTick_returns_zero_for_non_positive_prices()
    {
        Assert.Equal(0m, PseBoardLotService.RoundToValidTick(0m));
        Assert.Equal(0m, PseBoardLotService.RoundToValidTick(-3.25m));
    }

    [Theory]
    [InlineData(100.0, 95.0, 110.0, 2.0)]
    [InlineData(100.0, 97.5, 105.0, 2.0)]
    [InlineData(100.0, 100.0, 110.0, 0.0)]
    [InlineData(100.0, 105.0, 110.0, 0.0)]
    [InlineData(100.0, 90.0, 95.0, 0.0)]
    public void ComputeRiskReward_uses_entry_as_the_pivot(
        decimal entry,
        decimal stopLoss,
        decimal target,
        decimal expected)
    {
        Assert.Equal(expected, PseBoardLotService.ComputeRiskReward(entry, stopLoss, target));
    }

    [Fact]
    public void NormalizeTradePlan_rounds_every_leg_and_repairs_inverted_levels()
    {
        var plan = PseBoardLotService.NormalizeTradePlan(10.02m, 10.5m, 9.0m);

        Assert.True(plan.Target > plan.Entry);
        Assert.True(plan.Entry > plan.StopLoss);
        Assert.Equal(10.02m, plan.Entry);
        Assert.True(PseBoardLotService.ComputeRiskReward(plan.Entry, plan.StopLoss, plan.Target) > 0m);
    }

    [Fact]
    public void NormalizeTradePlan_keeps_a_valid_plan_inside_the_tick_grid()
    {
        var plan = PseBoardLotService.NormalizeTradePlan(100.0m, 95.04m, 110.03m);

        Assert.Equal(100.0m, plan.Entry);
        Assert.Equal(95.05m, plan.StopLoss);
        Assert.Equal(110.0m, plan.Target);
        Assert.Equal(0m, plan.Entry % PseBoardLotService.GetTickSize(plan.Entry));
        Assert.Equal(0m, plan.StopLoss % PseBoardLotService.GetTickSize(plan.StopLoss));
        Assert.Equal(0m, plan.Target % PseBoardLotService.GetTickSize(plan.Target));
    }

    [Fact]
    public void NormalizeTradePlan_nudges_a_colliding_target_onto_the_next_tick()
    {
        // 4.99 and 5.00 share the 0.01 tick, so the guard has to step exactly one tick.
        var plan = PseBoardLotService.NormalizeTradePlan(4.99m, 4.9m, 4.99m);

        Assert.True(plan.Target > plan.Entry);
        Assert.Equal(4.99m, plan.Entry);
        Assert.Equal(5.0m, plan.Target);
        Assert.Equal(0m, plan.Target % PseBoardLotService.GetTickSize(plan.Target));
    }

    [Fact]
    public void GetStaticPriceLimits_uses_a_plus_50_ceiling_and_minus_30_floor()
    {
        var limits = PseBoardLotService.GetStaticPriceLimits(100m);

        Assert.Equal(150m, limits.StaticCeiling);
        Assert.Equal(70m, limits.StaticFloor);
        Assert.Empty(PseBoardLotService.ValidateAgainstStaticThresholds(100m, 120m, 95m, 145m));
    }

    [Fact]
    public void NormalizeTradePlanWithinStaticLimits_clamps_an_intraday_target_inside_the_band()
    {
        // A 90% target on a ₱100 close breaches the +50% ceiling the exchange enforces.
        var plan = PseBoardLotService.NormalizeTradePlanWithinStaticLimits(100m, 101m, 97m, 190m);

        Assert.Equal(150m, plan.Target);
        Assert.True(plan.Entry > plan.StopLoss);
        Assert.Empty(PseBoardLotService.ValidateAgainstStaticThresholds(100m, plan.Entry, plan.StopLoss, plan.Target));
    }

    [Fact]
    public void RoundSharesToBoardLot_floors_a_budget_to_whole_board_lots()
    {
        // ₱100,000 at ₱28.50 buys 3,508 shares; the board lot is 100, so 3,500 shares.
        Assert.Equal(3_500, PseBoardLotService.RoundSharesToBoardLot(100_000m, 28.5m));

        // A ₱2,000 budget cannot fund a single 100-share lot of a ₱28.50 name.
        Assert.Equal(0, PseBoardLotService.RoundSharesToBoardLot(2_000m, 28.5m));
        Assert.Equal(0, PseBoardLotService.RoundSharesToBoardLot(0m, 88m));
    }

    [Fact]
    public void ComputeNetRiskReward_charges_fees_on_both_legs()
    {
        var gross = PseBoardLotService.ComputeRiskReward(100m, 97m, 106m);
        var net = PseBoardLotService.ComputeNetRiskReward(100m, 97m, 106m);

        Assert.Equal(2.0m, gross);
        Assert.True(net < gross);
        Assert.Equal(0m, PseBoardLotService.ComputeNetRiskReward(100m, 100m, 106m));
    }

    [Fact]
    public void ComputeNetRiskPerShare_adds_the_round_trip_friction_to_the_stop_distance()
    {
        // PHP 5.00 of stop distance plus the two legs' friction: 0.295% paid on the PHP 100
        // entry and 0.395% (which includes the 0.10% stock transaction tax) on the PHP 95 stop.
        Assert.Equal(5.6703m, PseBoardLotService.ComputeNetRiskPerShare(100m, 95m));
        Assert.Equal(9.2705m, PseBoardLotService.ComputeNetRewardPerShare(100m, 110m));
        Assert.Equal(0m, PseBoardLotService.ComputeNetRiskPerShare(100m, 100m));
        Assert.Equal(0m, PseBoardLotService.ComputeNetRewardPerShare(100m, 100m));
    }

    [Fact]
    public void ComputeBreakEvenWinRate_is_much_higher_than_the_naive_ratio()
    {
        // A gross 1:2.0 swing is only about 1:1.6349 after fees, so it needs ~37.95%, not 33.3%.
        var breakEven = PseBoardLotService.ComputeBreakEvenWinRate(100m, 95m, 110m);

        Assert.Equal(0.3795m, breakEven);
        Assert.True(breakEven > 0.37m && breakEven < 0.39m);
        Assert.Equal(1m, PseBoardLotService.ComputeBreakEvenWinRate(100m, 100m, 110m));
    }

    [Theory]
    [InlineData(0.5, 2.0, 0.5)]
    [InlineData(0.3333, 2.0, -0.0001)]
    [InlineData(0.6, 1.5, 0.5)]
    [InlineData(0.0, 2.0, -1.0)]
    public void ComputeExpectedValueR_uses_p_times_net_reward_minus_one_minus_p(
        double probability,
        double netRiskReward,
        double expected)
    {
        var value = PseBoardLotService.ComputeExpectedValueR((decimal)probability, (decimal)netRiskReward);

        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData(0.5, 2.0, 0.0625)]
    [InlineData(0.3, 1.5, 0.0)]
    [InlineData(0.95, 10.0, 0.2)]
    public void ComputeQuarterKellyFraction_scales_the_edge_and_clamps_the_stake(
        double probability,
        double netRiskReward,
        double expected)
    {
        var fraction = PseBoardLotService.ComputeQuarterKellyFraction((decimal)probability, (decimal)netRiskReward);

        Assert.Equal((decimal)expected, fraction);
    }

    [Fact]
    public void SizePositionByRiskBudget_floors_to_whole_board_lots()
    {
        // PHP 1,000,000 of equity risking 1% = PHP 10,000. At PHP 5.67025 of all-in risk per
        // share that is 1,763 shares, floored to 176 ten-share board lots.
        var position = PseBoardLotService.SizePositionByRiskBudget(
            accountEquityPhp: 1_000_000m,
            maxRiskFraction: 0.01m,
            entryPrice: 100m,
            stopLoss: 95m,
            targetPrice: 110m,
            avgTurnover20dPhp: 25_000_000m);

        Assert.Equal(10, position.BoardLotSize);
        Assert.Equal(1_760, position.Shares);
        Assert.Equal(176_000m, position.NotionalPhp);
        Assert.Equal(9_979.73m, position.NetRiskPhp);
        Assert.Equal(16_316.08m, position.NetRewardPhp);
        Assert.False(position.LiquidityCapped);
    }

    [Fact]
    public void SizePositionByRiskBudget_caps_the_notional_at_twenty_percent_of_equity()
    {
        var position = PseBoardLotService.SizePositionByRiskBudget(
            accountEquityPhp: 1_000_000m,
            maxRiskFraction: 0.05m,
            entryPrice: 100m,
            stopLoss: 95m,
            targetPrice: 110m,
            avgTurnover20dPhp: 25_000_000m);

        Assert.Equal(2_000, position.Shares);
        Assert.Equal(200_000m, position.NotionalPhp);
    }

    [Fact]
    public void SizePositionByRiskBudget_refuses_a_position_the_liquidity_cap_cannot_place()
    {
        // 2% of a PHP 10,000 turnover day is PHP 200: not even one ten-share board lot.
        var position = PseBoardLotService.SizePositionByRiskBudget(
            accountEquityPhp: 1_000_000m,
            maxRiskFraction: 0.01m,
            entryPrice: 100m,
            stopLoss: 95m,
            targetPrice: 110m,
            avgTurnover20dPhp: 10_000m);

        Assert.Equal(0, position.Shares);
        Assert.Equal(0m, position.NotionalPhp);
    }
}
