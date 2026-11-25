using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The walk-forward replayer is only trustworthy if it never reads a bar before its session
/// closes and always pays the round trip. These tests drive it with a deterministic stub
/// series and assert the causal property directly: appending a future crash bar must not
/// rewrite a single past trade.
/// </summary>
public sealed class BacktestServiceTests
{
    private const int BarCount = 240;

    [Fact]
    public async Task RunAsync_refuses_to_invent_a_curve_when_the_feed_has_no_history()
    {
        var service = CreateService(Array.Empty<OhlcvBar>());

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900));

        Assert.Contains("No live PSE history", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_fails_when_no_session_clears_the_entry_rules()
    {
        var service = CreateService(BuildFlatBars(BarCount));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900));

        Assert.Contains("cleared the WEEK entry rules", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_replays_whole_board_lots_and_pays_the_round_trip()
    {
        var service = CreateService(BuildOscillatingBars(BarCount));

        var result = await service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        Assert.Equal("WEEK", result.Horizon);
        Assert.Equal(BacktestService.StartingCapitalPhp, result.StartingCapitalPhp);
        Assert.True(result.TotalTrades >= 1, "the replay closed no trade at all");
        Assert.NotEmpty(result.EquityCurve);
        Assert.True(result.TotalFeesPaidPhp > 0m);
        Assert.Equal(result.EndingEquityPhp, result.EquityCurve[^1].EquityPhp);

        foreach (var trade in result.Trades)
        {
            var boardLot = PseBoardLotService.GetBoardLotSize(trade.EntryPrice);
            Assert.Equal(0, trade.Shares % boardLot);
            Assert.Contains(
                trade.ExitReason,
                new[] { "STOP", "TARGET", "TIME", "BREAKEVEN", "TRAIL_EMA20" });
            Assert.True(trade.BarsHeld >= 1);
            Assert.True(trade.ExitDate >= trade.EntryDate);

            var expected = PseBoardLotService.ComputeRealizedNetPnlPhp(
                trade.Shares,
                trade.EntryPrice,
                trade.ExitPrice);
            Assert.Equal(expected - trade.SlippagePhp, trade.NetPnlPhp);
        }
    }

    [Fact]
    public async Task RunAsync_keeps_past_decisions_identical_when_a_future_bar_is_appended()
    {
        var withoutFuture = CreateService(BuildOscillatingBars(BarCount));
        var past = await withoutFuture.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        var withFuture = CreateService(BuildOscillatingBars(BarCount + 1, crashLastBar: true));
        var future = await withFuture.RunAsync(SignalHorizon.WEEK, LastDate(BarCount + 1), lookbackDays: 900);

        var closedBeforeCrash = future.Trades
            .Where(trade => trade.ExitDate <= past.EndDate)
            .ToList();

        Assert.NotEmpty(past.Trades);
        Assert.Equal(past.Trades, closedBeforeCrash);
    }

    [Fact]
    public async Task RunAsync_keeps_past_decisions_identical_when_the_index_turns_bearish_later()
    {
        var past = await CreateService(BuildOscillatingBars(BarCount), BuildIndexBars(BarCount))
            .RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        var withFuture = CreateService(
            BuildOscillatingBars(BarCount + 1, crashLastBar: true),
            BuildIndexBars(BarCount + 1, crashLastBar: true));

        var future = await withFuture.RunAsync(SignalHorizon.WEEK, LastDate(BarCount + 1), lookbackDays: 900);

        var closedBeforeCrash = future.Trades
            .Where(trade => trade.ExitDate <= past.EndDate)
            .ToList();

        Assert.NotEmpty(past.Trades);
        Assert.Equal(past.Trades, closedBeforeCrash);
    }

    [Fact]
    public async Task RunAsync_benchmarks_the_replay_against_holding_the_index()
    {
        var service = CreateService(BuildOscillatingBars(BarCount), BuildIndexBars(BarCount));

        var result = await service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        Assert.NotNull(result.BenchmarkReturnPct);
        Assert.NotNull(result.ExcessReturnPct);
        Assert.NotEmpty(result.EquityCurve);
        Assert.All(result.EquityCurve, point => Assert.NotNull(point.BenchmarkEquityPhp));
        Assert.Equal(
            BacktestService.StartingCapitalPhp,
            result.EquityCurve[0].BenchmarkEquityPhp!.Value);

        // Alpha is measured on the slippage-adjusted return, so the two lines are compared on
        // the desk's own after-friction number rather than the flattering pre-slippage one.
        Assert.Equal(
            decimal.Round(
                result.NetReturnAfterSlippagePct - result.BenchmarkReturnPct!.Value,
                2,
                MidpointRounding.AwayFromZero),
            result.ExcessReturnPct!.Value);

        Assert.NotNull(result.AnnualizedSharpeRatio);
        Assert.True(result.NetReturnAfterSlippagePct <= result.NetReturnPct);
        Assert.NotEqual(0m, result.CagrPct);
    }

    [Fact]
    public async Task RunAsync_charges_the_slippage_estimate_on_the_published_return()
    {
        var service = CreateService(BuildOscillatingBars(BarCount), BuildIndexBars(BarCount));

        var result = await service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        Assert.True(result.TotalSlippagePhp > 0m, "no round-trip slippage was charged");

        // Slippage is debited from cash when a clip opens, so it is already inside the equity
        // curve and the two return figures must agree to the cent. Deducting it a second time
        // from the published return used to double-count the friction.
        Assert.Equal(result.NetReturnPct, result.NetReturnAfterSlippagePct);

        // The published return is exactly the curve the book walked, and it has already paid the
        // friction: putting the slippage back on the curve lifts the return, which proves the
        // cost reached the equity line instead of only the headline.
        var grossOfSlippagePct = decimal.Round(
            (result.EndingEquityPhp + result.TotalSlippagePhp - BacktestService.StartingCapitalPhp) /
            BacktestService.StartingCapitalPhp * 100m,
            2,
            MidpointRounding.AwayFromZero);
        Assert.Equal(
            decimal.Round(
                (result.EndingEquityPhp - BacktestService.StartingCapitalPhp) /
                BacktestService.StartingCapitalPhp * 100m,
                2,
                MidpointRounding.AwayFromZero),
            result.NetReturnPct);
        Assert.True(grossOfSlippagePct > result.NetReturnPct);
    }

    [Fact]
    public async Task RunAsync_funds_no_new_long_while_the_index_is_bearish()
    {
        var stock = BuildOscillatingBars(BarCount);
        var gateOff = CreateService(stock, BuildIndexBars(BarCount));
        var control = await gateOff.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        Assert.True(control.TotalTrades >= 1, "the control run never traded, so the gate test proves nothing");

        var gateOn = CreateService(stock, BuildIndexBars(BarCount, crashFromIndex: 200));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => gateOn.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900));

        Assert.Contains("cleared the WEEK entry rules", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_ratchets_the_stop_to_break_even_after_a_one_r_rally()
    {
        var bars = BuildLadderBars(BarCount, rallyPct: 12m, rallySessions: 20);
        var service = CreateService(bars);

        var result = await service.RunAsync(SignalHorizon.MONTH, LastDate(bars.Count), lookbackDays: 900);

        var trade = result.Trades.FirstOrDefault(candidate => candidate.ExitReason == "BREAKEVEN");
        Assert.True(
            trade is not null,
            string.Join(" | ", result.Trades.Select(t =>
                $"{t.EntryDate}@{t.EntryPrice}->{t.ExitDate}@{t.ExitPrice} {t.ExitReason} held {t.BarsHeld} R{t.NetR}")));
        Assert.True(trade!.ExitPrice > trade.EntryPrice, "the break-even stop must still bank a small gain");
        Assert.True(trade.BarsHeld > 1);
    }

    [Fact]
    public async Task RunAsync_trails_the_stop_behind_ema20_and_publishes_it()
    {
        var bars = BuildLadderBars(BarCount, rallyPct: 17m, rallySessions: 20);
        var service = CreateService(bars);

        var result = await service.RunAsync(SignalHorizon.MONTH, LastDate(bars.Count), lookbackDays: 900);

        var trade = result.Trades.FirstOrDefault(candidate => candidate.ExitReason == "TRAIL_EMA20");
        Assert.True(
            trade is not null,
            string.Join(" | ", result.Trades.Select(t =>
                $"{t.EntryDate}@{t.EntryPrice}->{t.ExitDate}@{t.ExitPrice} {t.ExitReason} held {t.BarsHeld} R{t.NetR}")));
        Assert.True(trade!.ExitPrice > trade.EntryPrice, "the EMA20 trail must lock in a profit");
        Assert.True(
            trade.ExitPrice >= PseBoardLotService.ComputeBreakEvenStopPrice(trade.EntryPrice),
            "the trailed stop must never sit below the break-even tick");
        Assert.True(trade.NetR > 0m, $"the trailed exit realized only {trade.NetR}R");
    }

    [Fact]
    public async Task RunAsync_holds_several_names_at_once_without_breaking_the_position_cap()
    {
        var bars = BuildOscillatingBars(BarCount);
        var service = CreatePortfolioService(BuildPortfolio(bars, count: 8), bars);

        var result = await service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        // A one-slot blotter used to turn every setup into a serial bet. The book now holds a
        // basket, so the same tape must produce overlapping clips — but never more than the cap.
        Assert.True(
            MaxConcurrent(result.Trades) == BacktestService.MaxOpenPositions,
            $"the replay opened only {MaxConcurrent(result.Trades)} clip(s) at a time: " +
            string.Join(" | ", result.Trades.Select(t => $"{t.Ticker} {t.EntryDate}->{t.ExitDate}")));

        Assert.True(
            result.Trades.Select(trade => trade.Ticker).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2,
            "the multi-slot book traded a single name, so nothing about slots was exercised");
    }

    [Fact]
    public async Task RunAsync_keeps_one_sector_inside_its_name_and_exposure_caps()
    {
        var bars = BuildOscillatingBars(BarCount);
        var service = CreatePortfolioService(BuildPortfolio(bars, count: 6, sharedSector: "Banks"), bars);

        var result = await service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        Assert.True(result.TotalTrades >= 1, "the crowded-sector run never traded, so the cap proves nothing");

        // Every name sits in Banks, so the per-sector name cap — not the board — decides how many
        // of them the book may hold at once. Two clips fit under the 35% notional ceiling; a third
        // would have been a single-theme bet.
        Assert.True(
            MaxConcurrent(result.Trades) == BacktestService.MaxPositionsPerSector,
            $"the book held {MaxConcurrent(result.Trades)} banking names against a cap of " +
            $"{BacktestService.MaxPositionsPerSector}: " +
            string.Join(" | ", result.Trades.Select(t => $"{t.Ticker} {t.EntryDate}->{t.ExitDate}")));

        // Every name sits in Banks, so the per-sector name cap and the notional ceiling are the
        // only things standing between the book and a single-theme bet.
        Assert.True(
            MaxConcurrent(result.Trades) <= BacktestService.MaxPositionsPerSector,
            $"the book held {MaxConcurrent(result.Trades)} banking names against a cap of {BacktestService.MaxPositionsPerSector}");

        foreach (var trade in result.Trades)
        {
            var concurrentOutlayPhp = result.Trades
                .Where(other => other.EntryDate <= trade.EntryDate && other.ExitDate > trade.EntryDate)
                .Sum(other => PseBoardLotService.ComputeBuyTotalCostPhp(other.Shares, other.EntryPrice));

            Assert.True(
                concurrentOutlayPhp <= BacktestService.StartingCapitalPhp *
                (BacktestService.MaxSectorExposurePct / 100m),
                $"one sector carried {concurrentOutlayPhp:N2} against a ceiling of " +
                $"{BacktestService.StartingCapitalPhp * (BacktestService.MaxSectorExposurePct / 100m):N2}");
        }
    }

    [Fact]
    public async Task RunAsync_never_spends_more_cash_than_the_book_holds()
    {
        var bars = BuildOscillatingBars(BarCount);
        var service = CreatePortfolioService(BuildPortfolio(bars, count: 8), bars);

        var result = await service.RunAsync(SignalHorizon.WEEK, LastDate(BarCount), lookbackDays: 900);

        Assert.True(result.TotalTrades >= 1);

        // Sizing is derived from equity, so a fully invested book can compute a clip the free
        // cash cannot settle. The replay must trim it to whole lots — fees and the entry
        // slippage included — instead of overdrawing the account.
        foreach (var trade in result.Trades)
        {
            var concurrentOutlayPhp = result.Trades
                .Where(other => other.EntryDate <= trade.EntryDate && other.ExitDate > trade.EntryDate)
                .Sum(other => PseBoardLotService.ComputeBuyTotalCostPhp(other.Shares, other.EntryPrice)
                              + other.SlippagePhp);

            Assert.True(
                concurrentOutlayPhp <= BacktestService.StartingCapitalPhp,
                $"the book committed {concurrentOutlayPhp:N2} of a {BacktestService.StartingCapitalPhp:N2} account");
        }

        Assert.All(result.EquityCurve, point => Assert.True(point.EquityPhp > 0m));
    }

    private static BacktestService CreateService(
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<OhlcvBar>? indexBars = null)
    {
        var stocks = bars.Count == 0
            ? Array.Empty<StockMarketData>()
            : new[] { BuildStock("ICT", "Transportation", bars) };

        return CreatePortfolioService(stocks, bars, indexBars);
    }

    /// <summary>Replays an arbitrary board, so the slot, heat and sector caps can be exercised.</summary>
    private static BacktestService CreatePortfolioService(
        IReadOnlyList<StockMarketData> stocks,
        IReadOnlyList<OhlcvBar> bars,
        IReadOnlyList<OhlcvBar>? indexBars = null)
    {
        var lastBar = stocks
            .SelectMany(stock => stock.Bars)
            .Select(bar => bar.Date)
            .DefaultIfEmpty(LastDate(BarCount))
            .Max();

        var batch = new MarketDataBatch(
            lastBar,
            PseiClose: 8_500m,
            PseiSma50: 8_400m,
            PseiSma200: 8_100m,
            NetForeignFlowPhp: 1_200_000_000m,
            UsdPhp: 58.3m,
            MarketRegime: MarketRegime.BULLISH,
            RegimeNote: "Risk-on tape.",
            Stocks: stocks,
            Source: "unit-test",
            IndexBars: indexBars);

        return new BacktestService(new StubMarketDataProvider(batch), new QuantitativeFilterService());
    }

    private static StockMarketData BuildStock(string ticker, string sector, IReadOnlyList<OhlcvBar> bars) =>
        new(
            ticker,
            CompanyName: $"{ticker} Holdings, Inc.",
            Sector: sector,
            Bars: bars,
            NetForeignTodayPhp: null,
            Disclosures: Array.Empty<StockDisclosure>());

    /// <summary>
    /// A board whose names all share the same qualifying tape — either spread over their own
    /// sectors or crammed into one — so the caps, not the setups, decide what is funded.
    /// </summary>
    private static IReadOnlyList<StockMarketData> BuildPortfolio(
        IReadOnlyList<OhlcvBar> bars,
        int count,
        string? sharedSector = null)
    {
        var tickers = new[] { "ICT", "SM", "BDO", "ALI", "JFC", "TEL", "MER", "AC" };

        return tickers
            .Take(Math.Clamp(count, 1, tickers.Length))
            .Select((ticker, index) => BuildStock(
                ticker,
                sharedSector ?? $"Sector {index + 1}",
                bars))
            .ToArray();
    }

    /// <summary>
    /// The most clips the book held at once, measured at each entry: a position counts while its
    /// interval still spans the session a new clip was opened on.
    /// </summary>
    private static int MaxConcurrent(IReadOnlyList<BacktestTradeDto> trades) =>
        trades.Count == 0
            ? 0
            : trades.Max(trade => trades.Count(
                other => other.EntryDate <= trade.EntryDate && other.ExitDate > trade.EntryDate));


    private static DateOnly LastDate(int barCount) => new DateOnly(2025, 1, 6).AddDays(barCount - 1);

    /// <summary>
    /// A choppy but rising tape: +0.8, +0.4, -0.6 per session, so RSI stays inside the WEEK
    /// band while the 50/200-day averages slope up. Volume only expands around session 210,
    /// which is what makes the entry gate fire on a handful of dates.
    /// </summary>
    private static List<OhlcvBar> BuildOscillatingBars(int barCount, bool crashLastBar = false)
    {
        var bars = new List<OhlcvBar>(barCount);
        var date = new DateOnly(2025, 1, 6);
        var price = 50m;

        for (var index = 0; index < barCount; index++)
        {
            var close = price + ((index % 3) switch { 0 => 0.8m, 1 => 0.4m, _ => -0.6m });
            var volume = index is >= 210 and <= 215 ? 3_000_000L : 1_000_000L;

            bars.Add(new OhlcvBar(
                date.AddDays(index),
                Open: price,
                High: Math.Max(price, close) + 2.0m,
                Low: Math.Min(price, close) - 2.0m,
                Close: close,
                Volume: volume));

            price = close;
        }

        if (crashLastBar && bars.Count > 0)
        {
            var last = bars[^1];
            bars[^1] = last with
            {
                Open = last.Close,
                High = last.Close,
                Low = last.Close * 0.7m,
                Close = last.Close * 0.7m,
                Volume = 9_000_000
            };
        }

        return bars;
    }

    /// <summary>
    /// A rising PSEi so the causal regime gate stays open, with optional damage: a crash that
    /// starts at <paramref name="crashFromIndex"/> or a final crash bar. The index is the
    /// benchmark as well, so the same series proves both behaviours.
    /// </summary>
    private static List<OhlcvBar> BuildIndexBars(
        int barCount,
        int? crashFromIndex = null,
        bool crashLastBar = false)
    {
        var bars = new List<OhlcvBar>(barCount);
        var price = 7_600m;

        for (var index = 0; index < barCount; index++)
        {
            var crashing = crashFromIndex is int from && index >= from;
            var close = decimal.Round(price * (crashing ? 0.975m : 1.0004m), 2);

            bars.Add(new OhlcvBar(
                new DateOnly(2025, 1, 6).AddDays(index),
                Open: price,
                High: Math.Max(price, close) + 4m,
                Low: Math.Min(price, close) - 4m,
                Close: close,
                Volume: 1_000_000L));

            price = close;
        }

        if (crashLastBar && bars.Count > 0)
        {
            var last = bars[^1];
            bars[^1] = last with
            {
                Open = last.Close,
                High = last.Close,
                Low = last.Close * 0.6m,
                Close = last.Close * 0.6m
            };
        }

        return bars;
    }

    /// <summary>
    /// The oscillating tape with the volume expansion that opens the gate at sessions 210-215,
    /// then a linear rally to <paramref name="rallyPct"/> above session 214's close, then a
    /// fade back to flat. Highs sit a tick above the close so the rally cannot reach the target
    /// by accident: the exit ladder — not the target — has to decide the exit.
    /// </summary>
    private static List<OhlcvBar> BuildLadderBars(int barCount, decimal rallyPct, int rallySessions)
    {
        var bars = BuildOscillatingBars(barCount);
        var pivot = bars[214].Close;
        var rallyClose = decimal.Round(pivot * (1m + rallyPct / 100m), 2);
        const int rallyStart = 216;

        for (var index = rallyStart; index < rallyStart + rallySessions && index < bars.Count; index++)
        {
            var progress = (index - rallyStart + 1m) / rallySessions;
            var close = decimal.Round(pivot + ((rallyClose - pivot) * progress), 2);

            bars[index] = new OhlcvBar(
                bars[index].Date,
                Open: close - 0.2m,
                High: close + 0.2m,
                Low: close - 1.0m,
                Close: close,
                Volume: 1_000_000L);
        }

        var fadeIndex = rallyStart + rallySessions;
        if (fadeIndex < bars.Count)
        {
            bars[fadeIndex] = new OhlcvBar(
                bars[fadeIndex].Date,
                Open: pivot * 1.02m,
                High: pivot * 1.03m,
                Low: pivot * 1.001m,
                Close: pivot * 1.005m,
                Volume: 1_000_000L);
        }

        for (var index = fadeIndex + 1; index < bars.Count; index++)
        {
            bars[index] = new OhlcvBar(
                bars[index].Date,
                Open: pivot,
                High: pivot * 1.01m,
                Low: pivot * 0.99m,
                Close: pivot,
                Volume: 1_000_000L);
        }

        return bars;
    }

    private static List<OhlcvBar> BuildFlatBars(int barCount)
    {
        var bars = new List<OhlcvBar>(barCount);
        var date = new DateOnly(2025, 1, 6);

        for (var index = 0; index < barCount; index++)
        {
            bars.Add(new OhlcvBar(
                date.AddDays(index),
                Open: 50m,
                High: 50.2m,
                Low: 49.8m,
                Close: 50m,
                Volume: 1_000_000));
        }

        return bars;
    }

    private sealed class StubMarketDataProvider : IMarketDataProvider
    {
        private readonly MarketDataBatch _batch;

        public StubMarketDataProvider(MarketDataBatch batch)
        {
            _batch = batch;
        }

        public Task<MarketDataBatch> FetchAsync(DateOnly tradeDate, CancellationToken cancellationToken = default) =>
            Task.FromResult(_batch);
    }
}
