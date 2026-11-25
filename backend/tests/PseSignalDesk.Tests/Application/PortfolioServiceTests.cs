using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The blotter is the only place that may debit cash or move a stop, so every rule it
/// enforces is covered here against an in-memory book: board lots, free cash, the
/// single-position cap, portfolio heat, the exit ladder and the settle arithmetic.
/// </summary>
public sealed class PortfolioServiceTests
{
    [Fact]
    public async Task OpenTradeAsync_debits_the_fee_paid_buy_cost_and_records_the_all_in_risk()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio);

        var position = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));

        Assert.Equal("ICT", position.Ticker);
        Assert.Equal(1_000, position.Shares);
        Assert.Equal(100m, position.EntryPrice);
        Assert.Equal(95m, position.CurrentStopLoss);
        Assert.Equal(110m, position.TargetPrice);
        Assert.Equal(100_295m, position.BuyCostPhp);
        Assert.Equal(5_670.25m, position.InitialNetRiskPhp);
        Assert.Equal(899_705m, portfolio.Account.CashBalancePhp);
        Assert.Equal(nameof(PositionActionSignal.HOLD), position.Action);
        Assert.Single(portfolio.Trades);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_a_quantity_that_is_not_a_whole_board_lot()
    {
        var service = CreateService(new FakePortfolioRepository());

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(BuildOpenRequest(shares: 105)));

        Assert.Contains("board lots", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_an_order_the_free_cash_cannot_fund()
    {
        var portfolio = new FakePortfolioRepository();
        portfolio.Account.CashBalancePhp = 50_000m;
        var service = CreateService(portfolio);

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(BuildOpenRequest(shares: 500)));

        Assert.Contains("cash balance", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_a_position_above_the_single_position_cap()
    {
        var service = CreateService(new FakePortfolioRepository());

        // 2,000 shares at PHP 100.295 all-in is PHP 200,590: just over the 20% cap.
        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(BuildOpenRequest(shares: 2_000)));

        Assert.Contains("single-position cap", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_a_trade_that_breaches_the_portfolio_heat_cap()
    {
        var service = CreateService(new FakePortfolioRepository());

        await service.UpdateSettingsAsync(new PortfolioSettingsUpdateRequest(MaxPortfolioHeatPct: 1m));

        // PHP 100.295 in, PHP 89.6445 out at a PHP 90 stop: PHP 10.6505 per share, i.e. 1.065%.
        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(BuildOpenRequest(shares: 1_000, stopLoss: 90m)));

        Assert.Contains("portfolio heat", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateStopAsync_moves_the_stop_onto_the_break_even_tick()
    {
        var service = CreateService(new FakePortfolioRepository());
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(opened.EntryPrice);

        var updated = await service.UpdateStopAsync(opened.Id, new UpdateStopRequest(breakEven));

        Assert.Equal(breakEven, updated.CurrentStopLoss);
        Assert.Equal(breakEven, updated.BreakEvenStopPrice);
    }

    [Fact]
    public async Task UpdateStopAsync_does_not_report_a_breach_before_any_session_has_printed()
    {
        var service = CreateService(new FakePortfolioRepository());
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(opened.EntryPrice);

        var updated = await service.UpdateStopAsync(opened.Id, new UpdateStopRequest(breakEven));

        // With no stored session there is no low to breach: the mark is the entry price, so the
        // desk must not claim a stop-out it never observed.
        Assert.Equal(breakEven, updated.CurrentStopLoss);
        Assert.Equal(nameof(PositionActionSignal.HOLD), updated.Action);
        Assert.Equal(0, updated.SessionsHeld);
    }

    [Fact]
    public async Task UpdateStopAsync_rejects_a_stop_at_or_above_the_target()
    {
        var service = CreateService(new FakePortfolioRepository());
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.UpdateStopAsync(opened.Id, new UpdateStopRequest(110m)));

        Assert.Contains("target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloseTradeAsync_settles_the_sell_leg_credits_cash_and_records_the_R()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio);
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));

        var closed = await service.CloseTradeAsync(opened.Id, new CloseTradeRequest(110m));

        Assert.Equal(109_565.50m, closed.SellProceedsPhp);
        Assert.Equal(9_270.50m, closed.RealizedNetPnlPhp);
        Assert.Equal(1.6349m, closed.RealizedNetR);
        Assert.Equal(1_009_270.50m, portfolio.Account.CashBalancePhp);

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.CloseTradeAsync(opened.Id, new CloseTradeRequest(110m)));

        Assert.Contains("already closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSummaryAsync_reports_heat_realized_pnl_and_the_equity_curve()
    {
        var service = CreateService(new FakePortfolioRepository());
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        await service.CloseTradeAsync(opened.Id, new CloseTradeRequest(110m, new DateOnly(2026, 11, 24)));

        var summary = await service.GetSummaryAsync();

        // No stored session has printed since the entry, so the book is cash-only: one
        // settled winner at PHP 1,009,270.50 of equity and 100% win rate.
        Assert.Equal(1_009_270.50m, summary.TotalEquityPhp);
        Assert.Equal(9_270.50m, summary.RealizedNetPnlPhp);
        Assert.Equal(0m, summary.UnrealizedNetPnlPhp);
        Assert.Equal(0, summary.OpenPositions);
        Assert.Equal(1, summary.ClosedTrades);
        Assert.Equal(1m, summary.WinRate);
        Assert.Equal(0m, summary.OpenHeatPct);
        Assert.Single(summary.RealizedEquityCurve);
        Assert.Equal(1_009_270.50m, summary.RealizedEquityCurve[0].EquityPhp);
    }

    [Fact]
    public async Task UpdateSettingsAsync_clamps_percentages_and_resets_cash_on_an_empty_book()
    {
        var service = CreateService(new FakePortfolioRepository());

        var account = await service.UpdateSettingsAsync(new PortfolioSettingsUpdateRequest(
            StartingCapitalPhp: 250_000m,
            MaxRiskPerTradePct: 99m,
            SmallSizeRiskMultiplier: 0.01m,
            MaxPositionPct: 500m,
            MaxPortfolioHeatPct: 0m));

        Assert.Equal(250_000m, account.StartingCapitalPhp);
        Assert.Equal(250_000m, account.CashBalancePhp);
        Assert.Equal(10m, account.MaxRiskPerTradePct);
        Assert.Equal(0.1m, account.SmallSizeRiskMultiplier);
        Assert.Equal(100m, account.MaxPositionPct);
        Assert.Equal(1m, account.MaxPortfolioHeatPct);
    }

    [Fact]
    public async Task UpdateSettingsAsync_clamps_the_pod_shop_dials()
    {
        var service = CreateService(new FakePortfolioRepository());

        var account = await service.UpdateSettingsAsync(new PortfolioSettingsUpdateRequest(
            MaxSectorExposurePct: 2m,
            MaxPositionsPerSector: 42,
            SoftDrawdownThresholdPct: 0.5m,
            HardDrawdownThresholdPct: 0.75m,
            TargetAnnualVolPct: 500m));

        // A 2% sector cap would block every sector on day one, so the floor is 5%; the
        // position count is 1-10 and the volatility target 5-60%.
        Assert.Equal(5m, account.MaxSectorExposurePct);
        Assert.Equal(10, account.MaxPositionsPerSector);
        Assert.Equal(1m, account.SoftDrawdownThresholdPct);
        Assert.Equal(2m, account.HardDrawdownThresholdPct);
        Assert.Equal(60m, account.TargetAnnualVolPct);
    }

    [Fact]
    public async Task UpdateSettingsAsync_rejects_a_hard_drawdown_below_the_soft_one()
    {
        var service = CreateService(new FakePortfolioRepository());

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.UpdateSettingsAsync(new PortfolioSettingsUpdateRequest(
                SoftDrawdownThresholdPct: 15m,
                HardDrawdownThresholdPct: 12m)));

        Assert.Contains("hard drawdown", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSummaryAsync_measures_the_drawdown_from_the_stored_equity_peak()
    {
        var portfolio = new FakePortfolioRepository();
        // The book peaked at PHP 1.10m and is now flat at the PHP 1.00m bankroll: a 9.09%
        // drawdown, i.e. defensive but not yet halted.
        portfolio.Account.PeakEquityPhp = 1_100_000m;

        var summary = await CreateService(portfolio).GetSummaryAsync();

        Assert.Equal(1_000_000m, summary.TotalEquityPhp);
        Assert.Equal(1_100_000m, summary.PeakEquityPhp);
        Assert.Equal(9.09m, summary.DrawdownPct);
        Assert.Equal(RiskGovernorState.Defensive, summary.RiskPosture);
        Assert.Contains(summary.Alerts, alert => alert.Contains("soft limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSummaryAsync_halts_the_posture_past_the_hard_drawdown_limit()
    {
        var portfolio = new FakePortfolioRepository();
        portfolio.Account.PeakEquityPhp = 1_200_000m;

        var summary = await CreateService(portfolio).GetSummaryAsync();

        // PHP 1.00m against a PHP 1.20m peak is a 16.67% drawdown: capital preservation.
        Assert.Equal(16.67m, summary.DrawdownPct);
        Assert.Equal(RiskGovernorState.Halted, summary.RiskPosture);
        Assert.Contains(summary.Alerts, alert => alert.Contains("hard limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetSummaryAsync_reports_expectancy_profit_factor_and_payoff_in_r()
    {
        var service = CreateService(new FakePortfolioRepository());

        var winner = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        await service.CloseTradeAsync(winner.Id, new CloseTradeRequest(110m));

        var loser = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        await service.CloseTradeAsync(loser.Id, new CloseTradeRequest(90m));

        var summary = await service.GetSummaryAsync();

        // +1.6349R and -1.8783R settle into a slightly negative edge with a 0.87 profit factor.
        Assert.Equal(-0.1217m, summary.ExpectancyR);
        Assert.Equal(0.87m, summary.ProfitFactor);
        Assert.Equal(0.87m, summary.PayoffRatio);
    }

    [Fact]
    public async Task TrimTradeAsync_banks_a_whole_lot_slice_and_splits_the_cost_proportionally()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio);
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));

        // The desk offers to bank half the position: 500 shares in whole board lots.
        Assert.Equal(500, opened.RecommendedTrimShares);

        var trimmed = await service.TrimTradeAsync(opened.Id, new TrimTradeRequest(Shares: 500, ExitPrice: 110m));

        Assert.Equal(500, trimmed.RemainingPosition.Shares);
        Assert.Equal(50_147.50m, trimmed.RemainingPosition.BuyCostPhp);
        Assert.Equal(2_835.12m, trimmed.RemainingPosition.InitialNetRiskPhp);

        // Banking above the break-even tick ratchets the remainder's stop up to it.
        Assert.Equal(
            PseBoardLotService.ComputeBreakEvenStopPrice(opened.EntryPrice),
            trimmed.RemainingPosition.CurrentStopLoss);

        Assert.Equal(500, trimmed.SettledSlice.Shares);
        Assert.Equal(54_782.75m, trimmed.SettledSlice.SellProceedsPhp);
        Assert.Equal(4_635.25m, trimmed.SettledSlice.RealizedNetPnlPhp);
        Assert.Equal(1.6349m, trimmed.SettledSlice.RealizedNetR);
        Assert.Contains("Trimmed 500 of 1000 shares", trimmed.SettledSlice.Notes, StringComparison.Ordinal);

        // PHP 899,705 after the buy, plus the PHP 54,782.75 the slice sold for.
        Assert.Equal(954_487.75m, portfolio.Account.CashBalancePhp);
        Assert.Equal(2, portfolio.Trades.Count);
        Assert.Single(portfolio.Trades, trade => trade.Status == PortfolioTradeStatus.OPEN);
    }

    [Fact]
    public async Task OpenTradeAsync_only_recommends_a_trim_that_is_a_legal_lot_at_the_marked_price()
    {
        // Bought at PHP 5.00 in 100-share lots; the mark has slipped to PHP 4.99, where the
        // board lot coarsens to 1,000 shares. Half of this 3,000-share position (1,500) is
        // unsellable at that price, so the desk recommends the largest legal slice instead.
        var service = CreateService(new FakePortfolioRepository(), BuildMetric(close: 4.99m));

        var opened = await service.OpenTradeAsync(
            BuildOpenRequest(shares: 3_000, entryPrice: 5m, stopLoss: 4.5m, targetPrice: 6m));

        // A freshly opened position is marked at its own entry, so read the guidance from the
        // blotter once the session has marked it lower.
        var position = (await service.GetSummaryAsync()).Positions.Single();

        Assert.Equal(4.99m, position.LastPrice);
        Assert.Equal(1_000, position.RecommendedTrimShares);

        // The recommendation is executable as-is: a whole exit lot that leaves a whole entry lot.
        var trimmed = await service.TrimTradeAsync(
            opened.Id, new TrimTradeRequest(Shares: position.RecommendedTrimShares, ExitPrice: 4.99m));

        Assert.Equal(2_000, trimmed.RemainingPosition.Shares);
        Assert.Equal(1_000, trimmed.SettledSlice.Shares);
    }

    [Fact]
    public async Task TrimTradeAsync_rejects_a_slice_that_is_not_a_whole_board_lot()
    {
        var service = CreateService(new FakePortfolioRepository());
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.TrimTradeAsync(opened.Id, new TrimTradeRequest(Shares: 505, ExitPrice: 110m)));

        Assert.Contains("board lots", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrimTradeAsync_rejects_closing_the_whole_position_as_a_trim()
    {
        var service = CreateService(new FakePortfolioRepository());
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.TrimTradeAsync(opened.Id, new TrimTradeRequest(Shares: 1_000, ExitPrice: 110m)));

        Assert.Contains("different action", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrimTradeAsync_refuses_to_leave_an_illegal_board_lot_behind()
    {
        var service = CreateService(new FakePortfolioRepository());

        // Bought at PHP 5 in 100-share lots; trimming 50 at PHP 100 leaves 950 shares, which is
        // not a whole 100-share lot at the price the position was bought at.
        var opened = await service.OpenTradeAsync(
            BuildOpenRequest(shares: 1_000, entryPrice: 5m, stopLoss: 4m, targetPrice: 8m));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.TrimTradeAsync(opened.Id, new TrimTradeRequest(Shares: 50, ExitPrice: 100m)));

        Assert.Contains("left behind", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TrimTradeAsync_rejects_a_position_that_is_already_closed()
    {
        var service = CreateService(new FakePortfolioRepository());
        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        await service.CloseTradeAsync(opened.Id, new CloseTradeRequest(110m));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.TrimTradeAsync(opened.Id, new TrimTradeRequest(Shares: 500, ExitPrice: 110m)));

        Assert.Contains("already closed", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenTradeAsync_pyramids_into_an_open_winner_and_keeps_one_position()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio, BuildMetric(close: 130m));

        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(opened.EntryPrice);
        await service.UpdateStopAsync(opened.Id, new UpdateStopRequest(breakEven));

        var merged = await service.OpenTradeAsync(
            BuildOpenRequest(shares: 500, entryPrice: 105m, stopLoss: 95m, targetPrice: 120m));

        // One position, averaged up on the actual fee-paid costs, still targeting PHP 110. The
        // all-in risk is recomputed from the merged book against the ratcheted break-even stop.
        Assert.Single(portfolio.Trades);
        Assert.Equal(1, merged.PyramidCount);
        Assert.Equal(1_500, merged.Shares);
        Assert.Equal(101.7m, merged.EntryPrice);
        Assert.Equal(152_949.88m, merged.BuyCostPhp);
        Assert.Equal(2_496.53m, merged.InitialNetRiskPhp);
        Assert.Equal(breakEven, merged.CurrentStopLoss);
        Assert.Equal(110m, merged.TargetPrice);
        Assert.Equal(847_050.12m, portfolio.Account.CashBalancePhp);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_a_pyramid_add_before_the_stop_is_at_break_even()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio, BuildMetric(close: 130m));

        await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(
                BuildOpenRequest(shares: 500, entryPrice: 105m, stopLoss: 95m, targetPrice: 120m)));

        Assert.Contains("break-even tick", error.Message, StringComparison.Ordinal);
        Assert.Single(portfolio.Trades);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_a_pyramid_add_below_one_r()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio, BuildMetric(close: 101m));

        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(opened.EntryPrice);
        await service.UpdateStopAsync(opened.Id, new UpdateStopRequest(breakEven));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(
                BuildOpenRequest(shares: 500, entryPrice: 105m, stopLoss: 95m, targetPrice: 120m)));

        Assert.Contains("unrealized profit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_an_average_down_on_an_open_position()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio, BuildMetric(close: 130m));

        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(opened.EntryPrice);
        await service.UpdateStopAsync(opened.Id, new UpdateStopRequest(breakEven));

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(
                BuildOpenRequest(shares: 500, entryPrice: 97m, stopLoss: 92m, targetPrice: 115m)));

        Assert.Contains("No averaging down", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenTradeAsync_rejects_a_third_pyramid_add()
    {
        var portfolio = new FakePortfolioRepository();
        var service = CreateService(portfolio, BuildMetric(close: 130m));

        var opened = await service.OpenTradeAsync(BuildOpenRequest(shares: 1_000));
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(opened.EntryPrice);
        await service.UpdateStopAsync(opened.Id, new UpdateStopRequest(breakEven));

        portfolio.Trades.Single().PyramidCount = OrderTicketCalculator.MaxPyramidAdds;

        var error = await Assert.ThrowsAsync<PortfolioRuleViolationException>(
            () => service.OpenTradeAsync(
                BuildOpenRequest(shares: 500, entryPrice: 105m, stopLoss: 95m, targetPrice: 120m)));

        Assert.Contains("maximum of 2 pyramid", error.Message, StringComparison.Ordinal);
    }

    private static StockDailyMetric BuildMetric(decimal close, DateOnly? tradeDate = null) => new()
    {
        TradeDate = tradeDate ?? new DateOnly(2026, 11, 24),
        Ticker = "ICT",
        CompanyName = "International Container Terminal Services, Inc.",
        Sector = "Transportation",
        ClosePrice = close,
        HighPrice = close,
        LowPrice = close,
        Ema20 = close,
        AtrPct = 3m
    };

    private static PortfolioService CreateService(
        FakePortfolioRepository portfolio,
        StockDailyMetric? metric = null) =>
        new(portfolio, new EmptySignalRepository(metric));

    private static OpenTradeRequest BuildOpenRequest(
        int shares,
        decimal entryPrice = 100m,
        decimal stopLoss = 95m,
        decimal targetPrice = 110m) =>
        new(
            Ticker: "ICT",
            Shares: shares,
            EntryPrice: entryPrice,
            StopLoss: stopLoss,
            TargetPrice: targetPrice,
            Horizon: SignalHorizon.WEEK,
            EdgeVerdict: EdgeVerdict.TRADE,
            CompanyName: "International Container Terminal Services, Inc.",
            Sector: "Transportation");

    /// <summary>In-memory book with the same defaults the API creates on first read.</summary>
    private sealed class FakePortfolioRepository : IPortfolioRepository
    {
        private int _nextId = 1;

        public PortfolioAccount Account { get; } = new()
        {
            Id = 1,
            StartingCapitalPhp = 1_000_000m,
            CashBalancePhp = 1_000_000m
        };

        public List<PortfolioTrade> Trades { get; } = new();

        public Task<PortfolioAccount> GetOrCreateAccountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Account);

        public Task<PortfolioAccount> UpdateAccountAsync(
            PortfolioAccount account,
            CancellationToken cancellationToken = default)
        {
            Account.StartingCapitalPhp = account.StartingCapitalPhp;
            Account.CashBalancePhp = account.CashBalancePhp;
            Account.MaxRiskPerTradePct = account.MaxRiskPerTradePct;
            Account.SmallSizeRiskMultiplier = account.SmallSizeRiskMultiplier;
            Account.MaxPositionPct = account.MaxPositionPct;
            Account.MaxPortfolioHeatPct = account.MaxPortfolioHeatPct;
            Account.UpdatedAtUtc = account.UpdatedAtUtc;

            return Task.FromResult(Account);
        }

        public Task<IReadOnlyList<PortfolioTrade>> GetOpenTradesAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PortfolioTrade>>(
                Trades.Where(trade => trade.Status == PortfolioTradeStatus.OPEN).ToList());

        public Task<IReadOnlyList<PortfolioTrade>> GetClosedTradesAsync(
            int take = 100,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PortfolioTrade>>(
                Trades
                    .Where(trade => trade.Status == PortfolioTradeStatus.CLOSED)
                    .OrderByDescending(trade => trade.ClosedDate)
                    .Take(take)
                    .ToList());

        public Task<PortfolioTrade?> GetTradeAsync(int id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Trades.FirstOrDefault(trade => trade.Id == id));

        public Task<PortfolioTrade> AddTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default)
        {
            trade.Id = _nextId++;
            Trades.Add(trade);
            return Task.FromResult(trade);
        }

        public Task<PortfolioTrade> UpdateTradeAsync(
            PortfolioTrade trade,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(trade);
    }

    /// <summary>
    /// No stored sessions: marks fall back to the entry price and the latest-session helpers
    /// fall back to the trade date, which is exactly the offline behaviour under test. A test
    /// that needs a mark (the pyramid add has to be at least +1R) passes one metric instead.
    /// </summary>
    private sealed class EmptySignalRepository : ISignalRepository
    {
        private readonly StockDailyMetric? _metric;

        public EmptySignalRepository(StockDailyMetric? metric = null)
        {
            _metric = metric;
        }

        public Task<DailyMarketSnapshot?> GetSnapshotAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_metric is not null && _metric.TradeDate == tradeDate ? BuildSnapshot() : null);

        public Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_metric is null ? null : BuildSnapshot());

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StockDailyMetric>>(
                _metric is not null && _metric.TradeDate == tradeDate
                    ? new[] { _metric }
                    : Array.Empty<StockDailyMetric>());

        public Task<StockDailyMetric?> GetMetricAsync(
            DateOnly tradeDate,
            string ticker,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                _metric is not null &&
                _metric.TradeDate == tradeDate &&
                string.Equals(_metric.Ticker, ticker, StringComparison.OrdinalIgnoreCase)
                    ? _metric
                    : null);

        public Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(
                _metric is null ? Array.Empty<DateOnly>() : new[] { _metric.TradeDate });

        private DailyMarketSnapshot BuildSnapshot() => new()
        {
            TradeDate = _metric!.TradeDate,
            PseiClose = 8_500m,
            PseiSma50 = 8_400m,
            PseiSma200 = 8_100m,
            UsdPhp = 58.3m,
            MarketRegime = MarketRegime.BULLISH,
            RegimeNote = "unit-test"
        };

        public Task<IReadOnlyList<DateOnly>> GetRunDatesAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DateOnly>>(Array.Empty<DateOnly>());

        public Task<SignalRun?> GetLatestRunAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SignalRun?>(null);

        public Task<SignalRun?> GetRunAsync(
            DateOnly runDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SignalRun?>(null);

        public Task<SignalRun?> GetPreviousRunAsync(
            DateOnly beforeDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SignalRun?>(null);

        public Task<IReadOnlyList<SignalRun>> GetRunsForTickerAsync(
            string ticker,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SignalRun>>(Array.Empty<SignalRun>());

        public Task<IReadOnlyList<SignalRun>> GetAllRunsAsync(
            SignalHorizon? horizon = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<SignalRun>>(Array.Empty<SignalRun>());

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsRangeAsync(
            DateOnly fromDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StockDailyMetric>>(Array.Empty<StockDailyMetric>());

        public Task UpdateResolvedOutcomesAsync(
            IReadOnlyList<SignalItem> items,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveMarketDataAsync(
            MarketDataBatch batch,
            IReadOnlyList<StockDailyMetric> metrics,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task SaveSignalRunAsync(SignalRun run, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
