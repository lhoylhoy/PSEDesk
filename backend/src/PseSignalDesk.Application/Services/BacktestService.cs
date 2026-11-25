using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Causal walk-forward backtester. It replays the horizon's own rule set over the real PSE
/// tape with a single shared account: at most one position at a time, 1% of equity at risk,
/// whole board lots, the per-leg round trip (0.295% to buy, 0.395% to sell), the tick-spread/market-impact
/// slippage estimate charged, the pod-shop drawdown governor scaling new risk and the
/// mechanical exit ladder (+1R break-even, +1.5R EMA20 trail) managing every open trade.
/// Every entry is gated on the PSEi regime measured on the closed prefix, so a bearish tape
/// funds no new long, and nothing reads a bar before its session closes. The run is then
/// measured against buying and holding <c>PSEI.PS</c> over the exact same window. Nothing is
/// fabricated — if the live feed has no history, the run fails instead of producing a
/// modelled curve.
/// </summary>
public sealed class BacktestService
{
    public const decimal StartingCapitalPhp = 100_000m;
    public const decimal RiskPerTradePct = 1.0m;
    public const decimal MaxPositionPct = 20m;
    public const int DefaultLookbackCalendarDays = 900;

    /// <summary>Drawdown at which the replay starts scaling new risk, matching the live governor.</summary>
    public const decimal SoftDrawdownThresholdPct = 5m;

    /// <summary>Drawdown at which the replay funds no new risk at all.</summary>
    public const decimal HardDrawdownThresholdPct = 10m;

    /// <summary>Floor on the defensive multiplier, so a soft breach throttles rather than halts.</summary>
    public const decimal MinDrawdownRiskMultiplier = 0.50m;

    /// <summary>Fewest daily observations that make an annualised Sharpe meaningful.</summary>
    public const int MinimumSharpeSessions = 20;

    /// <summary>Sessions a year, for annualising the daily curve returns.</summary>
    public const int TradingSessionsPerYear = 252;

    /// <summary>Index bars needed before the causal PSEi regime gate is allowed to fire.</summary>
    private const int RegimeBarRequirement = 200;

    private readonly IMarketDataProvider _marketData;
    private readonly QuantitativeFilterService _quantFilter;

    public BacktestService(IMarketDataProvider marketData, QuantitativeFilterService quantFilter)
    {
        _marketData = marketData;
        _quantFilter = quantFilter;
    }

    /// <summary>
    /// Replays every session on or after <paramref name="startDate"/> (default: the earliest
    /// session with a qualifying candidate) up to the newest live session.
    /// </summary>
    public async Task<BacktestResultDto> RunAsync(
        SignalHorizon horizon,
        DateOnly asOfDate,
        int? lookbackDays = null,
        DateOnly? startDate = null,
        CancellationToken cancellationToken = default)
    {
        var profile = HorizonProfile.For(horizon);
        var batch = await _marketData.FetchAsync(
            asOfDate,
            Math.Max(QuantitativeFilterService.MinBarsRequired + 60, lookbackDays ?? DefaultLookbackCalendarDays),
            cancellationToken);

        if (batch.Stocks.Count == 0)
        {
            throw new MarketDataUnavailableException(
                $"No live PSE history is available through {asOfDate:yyyy-MM-dd}, so the {horizon} backtest cannot run.");
        }

        var candidatesByDate = BuildCandidates(batch, profile);

        if (candidatesByDate.Count == 0)
        {
            throw new MarketDataUnavailableException(
                $"No session in the fetched history cleared the {horizon} entry rules (trend alignment, RSI band, " +
                "volume expansion and the liquidity floor).");
        }

        return Simulate(batch, profile, candidatesByDate, startDate);
    }

    /// <summary>
    /// Evaluates each ticker's own history bar by bar, using only the bars that had already
    /// closed. The empirical base-rate study is disabled here: it is O(n²) per session and
    /// the walk-forward verdict must not consult future sessions to estimate an edge.
    /// </summary>
    private Dictionary<DateOnly, List<BacktestCandidate>> BuildCandidates(
        MarketDataBatch batch,
        HorizonProfile profile)
    {
        var byDate = new Dictionary<DateOnly, List<BacktestCandidate>>();
        var indexBars = batch.IndexBars ?? Array.Empty<OhlcvBar>();
        var minBars = Math.Max(profile.MinHistoryBars, QuantitativeFilterService.MinBarsRequired);

        foreach (var stock in batch.Stocks)
        {
            var bars = stock.Bars;
            if (bars.Count <= minBars)
            {
                continue;
            }

            var prefix = new List<OhlcvBar>(bars.Count);
            var indexPrefix = new List<OhlcvBar>(indexBars.Count);
            var indexCloses = new List<decimal>(indexBars.Count);
            var indexCursor = 0;

            for (var index = 0; index < bars.Count; index++)
            {
                prefix.Add(bars[index]);

                while (indexCursor < indexBars.Count && indexBars[indexCursor].Date <= bars[index].Date)
                {
                    indexPrefix.Add(indexBars[indexCursor]);
                    indexCloses.Add(indexBars[indexCursor].Close);
                    indexCursor++;
                }

                if (prefix.Count < minBars)
                {
                    continue;
                }

                // The regime gate is measured on the closed prefix only: the PSEi needs its
                // full 200 sessions before the desk trusts the average, and a bearish tape
                // (close under both SMA50 and SMA200) funds no new long.
                if (IsBearishIndexSession(indexPrefix, indexCloses))
                {
                    continue;
                }

                var metric = _quantFilter.EvaluateStock(
                    stock with { Bars = prefix },
                    bars[index].Date,
                    indexPrefix,
                    computeEmpiricalBaseRate: false);

                if (metric is null || !IsEligible(metric, profile))
                {
                    continue;
                }

                var candidate = BuildCandidate(metric, profile);
                if (candidate is null)
                {
                    continue;
                }

                if (!byDate.TryGetValue(bars[index].Date, out var list))
                {
                    list = new List<BacktestCandidate>();
                    byDate[bars[index].Date] = list;
                }

                list.Add(candidate);
            }
        }

        return byDate;
    }

    /// <summary>
    /// The horizon's entry gate, expressed only in terms of the metric of that session: an
    /// established uptrend, momentum that is not yet overbought, real volume expansion, a
    /// range wide enough to pay the fees, and honest liquidity.
    /// </summary>
    private static bool IsEligible(StockDailyMetric metric, HorizonProfile profile)
    {
        if (metric.AvgTurnover20dPhp < QuantitativeFilterService.LiquidityFloorPhp)
        {
            return false;
        }

        if (metric.ClosePrice <= metric.Sma50 || metric.ClosePrice <= metric.Sma200)
        {
            return false;
        }

        if (metric.Rsi14 < profile.RsiIdealLow || metric.Rsi14 > Math.Max(profile.RsiIdealHigh, 78m))
        {
            return false;
        }

        if (metric.VolumeRatioVs20d < 1.2m)
        {
            return false;
        }

        if (metric.AtrPct is not decimal atrPct || atrPct < 1.0m)
        {
            return false;
        }

        if (metric.WeinsteinStage is "STAGE_3" or "STAGE_4")
        {
            return false;
        }

        var flags = PseDisclosureClassifier.DetectTrapFlags(
            MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson));

        return !PseDisclosureClassifier.HasHardDisqualifier(flags);
    }

    private static BacktestCandidate? BuildCandidate(StockDailyMetric metric, HorizonProfile profile)
    {
        if (metric.Atr14 <= 0m)
        {
            return null;
        }

        var plan = PseBoardLotService.NormalizeTradePlan(
            metric.ClosePrice,
            metric.ClosePrice - (profile.AtrStopMultiplier * metric.Atr14),
            metric.ClosePrice + (profile.AtrTargetMultiplier * metric.Atr14));

        var netRiskReward = PseBoardLotService.ComputeNetRiskReward(plan.Entry, plan.StopLoss, plan.Target);
        if (netRiskReward < 1.2m)
        {
            return null;
        }

        return new BacktestCandidate(
            metric.Ticker,
            plan.Entry,
            plan.StopLoss,
            plan.Target,
            metric.QuantPreScore,
            metric.AvgTurnover20dPhp,
            metric.AtrPct);
    }

    /// <summary>
    /// Causal PSEi regime read on the closed prefix: above-average tape is tradable, and only
    /// a close under both the 50- and 200-session average counts as bearish.
    /// </summary>
    private static bool IsBearishIndexSession(
        IReadOnlyList<OhlcvBar> indexPrefix,
        IReadOnlyList<decimal> indexCloses)
    {
        if (indexPrefix.Count < RegimeBarRequirement || indexCloses.Count < RegimeBarRequirement)
        {
            return false;
        }

        var sma50 = QuantitativeFilterService.ComputeSma(indexCloses, 50);
        var sma200 = QuantitativeFilterService.ComputeSma(indexCloses, RegimeBarRequirement);
        if (sma50 is not decimal fast || sma200 is not decimal slow)
        {
            return false;
        }

        var close = indexPrefix[^1].Close;
        return close < fast && close < slow;
    }

    /// <summary>
    /// The single-account replay. One position at a time, entered at the session close of
    /// the first qualifying name in descending pre-score order, exited on the stop, the
    /// target or the horizon's time stop. A bar that touched both barriers fills the stop.
    /// </summary>
    private static BacktestResultDto Simulate(
        MarketDataBatch batch,
        HorizonProfile profile,
        Dictionary<DateOnly, List<BacktestCandidate>> candidatesByDate,
        DateOnly? startDate)
    {
        var barsByTicker = batch.Stocks.ToDictionary(
            stock => stock.Ticker,
            stock => stock.Bars.ToDictionary(bar => bar.Date),
            StringComparer.OrdinalIgnoreCase);

        var lastClose = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        var ema20ByTickerDate = BuildEma20Series(batch.Stocks);
        var dates = batch.Stocks
            .SelectMany(stock => stock.Bars)
            .Select(bar => bar.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        var indexBars = (batch.IndexBars ?? Array.Empty<OhlcvBar>())
            .Where(bar => bar.Close > 0m)
            .OrderBy(bar => bar.Date)
            .ToList();
        var indexCursor = 0;
        var lastIndexClose = 0m;
        var baseIndexClose = 0m;

        var cash = StartingCapitalPhp;
        var feesPaidPhp = 0m;
        var slippagePhp = 0m;
        var peakPhp = StartingCapitalPhp;
        var maxDrawdownPct = 0m;
        var lastEquityPhp = StartingCapitalPhp;
        var sessions = 0;
        DateOnly? firstDate = null;
        DateOnly lastDate = batch.TradeDate;

        var trades = new List<BacktestTradeDto>();
        var curve = new List<BacktestEquityPointDto>();
        SimulatedPosition? open = null;

        foreach (var date in dates)
        {
            if (startDate is DateOnly from && date < from)
            {
                continue;
            }

            // The index mark may only use the sessions that have already closed, so the
            // benchmark line is causal in exactly the same way as the equity line.
            while (indexCursor < indexBars.Count && indexBars[indexCursor].Date <= date)
            {
                lastIndexClose = indexBars[indexCursor].Close;
                indexCursor++;
            }

            foreach (var (ticker, series) in barsByTicker)
            {
                if (series.TryGetValue(date, out var closedBar))
                {
                    lastClose[ticker] = closedBar.Close;
                }
            }

            sessions++;
            firstDate ??= date;
            lastDate = date;

            if (open is not null && barsByTicker[open.Ticker].TryGetValue(date, out var session))
            {
                open.BarsHeld++;
                decimal? exitPrice = null;
                var exitReason = "TIME";

                if (session.Low <= open.CurrentStopLoss)
                {
                    exitPrice = open.CurrentStopLoss;
                    exitReason = open.StopMode == StopModes.Initial ? "STOP" : open.StopMode;
                }
                else if (session.High >= open.TargetPrice)
                {
                    exitPrice = open.TargetPrice;
                    exitReason = "TARGET";
                }
                else if (open.BarsHeld >= profile.ForwardWindowBars)
                {
                    exitPrice = session.Close;
                }

                if (exitPrice is decimal exit)
                {
                    cash += PseBoardLotService.ComputeSellNetProceedsPhp(open.Shares, exit);
                    feesPaidPhp += ComputeFeesPaidPhp(open.Shares, open.EntryPrice, exit);

                    // The clip's slippage was debited from cash at entry; subtract it from the
                    // trade's own P&L as well so the per-trade R is a net number.
                    var realizedPhp = PseBoardLotService.ComputeRealizedNetPnlPhp(open.Shares, open.EntryPrice, exit)
                                      - open.SlippagePhp;
                    trades.Add(new BacktestTradeDto(
                        open.Ticker,
                        open.EntryDate,
                        open.EntryPrice,
                        date,
                        exit,
                        open.Shares,
                        realizedPhp,
                        open.NetRiskPhp > 0m
                            ? decimal.Round(realizedPhp / open.NetRiskPhp, 4, MidpointRounding.AwayFromZero)
                            : 0m,
                        exitReason,
                        open.BarsHeld,
                        open.SlippagePhp));

                    open = null;
                }
                else
                {
                    RatchetStop(open, session, ema20ByTickerDate, date);
                }
            }

            if (open is null && candidatesByDate.TryGetValue(date, out var candidates) && candidates.Count > 0)
            {
                var best = candidates
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Ticker, StringComparer.Ordinal)
                    .First();

                // Pod-shop governor: new risk is cut on the way down the equity curve, so a
                // soft 5% drawdown halves the clip and a 10% drawdown stops funding trades.
                var drawdownPct = peakPhp > 0m
                    ? (peakPhp - lastEquityPhp) / peakPhp * 100m
                    : 0m;

                var riskFraction = RiskPerTradePct / 100m * ComputeDrawdownRiskMultiplier(drawdownPct);

                var sized = PseBoardLotService.SizePositionByRiskBudget(
                    cash,
                    riskFraction,
                    best.EntryPrice,
                    best.StopLoss,
                    best.TargetPrice,
                    best.TurnoverPhp,
                    MaxPositionPct / 100m);

                var costPhp = PseBoardLotService.ComputeBuyTotalCostPhp(sized.Shares, best.EntryPrice);

                // Slippage is a real cash cost of the clip (tick spread plus impact on both legs),
                // so it is debited from the book at entry instead of being deducted from the
                // reported return at the end: the equity curve, the drawdown governor and the
                // per-trade R must all see the same money.
                var slippageCostPhp = ComputeSlippageCostPhp(costPhp, best);

                if (sized.Shares > 0 && costPhp > 0m && costPhp + slippageCostPhp <= cash)
                {
                    cash -= costPhp + slippageCostPhp;
                    slippagePhp += slippageCostPhp;

                    open = new SimulatedPosition
                    {
                        Ticker = best.Ticker,
                        EntryDate = date,
                        EntryPrice = best.EntryPrice,
                        InitialStopLoss = best.StopLoss,
                        CurrentStopLoss = best.StopLoss,
                        TargetPrice = best.TargetPrice,
                        BreakEvenStopPrice = PseBoardLotService.ComputeBreakEvenStopPrice(best.EntryPrice),
                        StopMode = StopModes.Initial,
                        BuyCostPhp = costPhp,
                        SlippagePhp = slippageCostPhp,
                        Shares = sized.Shares,
                        NetRiskPhp = PseBoardLotService.ComputeInitialNetRiskPhp(
                            sized.Shares,
                            best.EntryPrice,
                            best.StopLoss)
                    };
                }
            }

            var openValuePhp = open is not null && lastClose.TryGetValue(open.Ticker, out var mark)
                ? PseBoardLotService.ComputeSellNetProceedsPhp(open.Shares, mark)
                : 0m;

            var equityPhp = decimal.Round(cash + openValuePhp, 2, MidpointRounding.AwayFromZero);
            peakPhp = Math.Max(peakPhp, equityPhp);

            var sessionDrawdownPct = peakPhp > 0m
                ? decimal.Round((peakPhp - equityPhp) / peakPhp * 100m, 2, MidpointRounding.AwayFromZero)
                : 0m;

            maxDrawdownPct = Math.Max(maxDrawdownPct, sessionDrawdownPct);

            decimal? benchmarkEquityPhp = null;
            if (lastIndexClose > 0m)
            {
                if (baseIndexClose <= 0m)
                {
                    baseIndexClose = lastIndexClose;
                }

                benchmarkEquityPhp = decimal.Round(
                    StartingCapitalPhp * (lastIndexClose / baseIndexClose),
                    2,
                    MidpointRounding.AwayFromZero);
            }

            lastEquityPhp = equityPhp;
            curve.Add(new BacktestEquityPointDto(date, equityPhp, sessionDrawdownPct, benchmarkEquityPhp));
        }

        return BuildResult(
            profile,
            trades,
            curve,
            feesPaidPhp,
            slippagePhp,
            maxDrawdownPct,
            sessions,
            firstDate ?? batch.TradeDate,
            lastDate);
    }

    private static decimal ComputeFeesPaidPhp(int shares, decimal entryPrice, decimal exitPrice)
    {
        var buyLeg = shares * entryPrice * PseBoardLotService.BuyLegFeeRate;
        var sellLeg = shares * exitPrice * PseBoardLotService.SellLegFeeRate;

        return decimal.Round(buyLeg + sellLeg, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Round-trip tick-spread plus market-impact estimate for one clip, in pesos. Slippage is
    /// reported separately from the fees so the reader can see how much of the edge the
    /// tape's microstructure eats on this name's turnover.
    /// </summary>
    private static decimal ComputeSlippageCostPhp(decimal outlayPhp, BacktestCandidate candidate)
    {
        var rate = PseBoardLotService.ComputeEstimatedSlippageRate(
            candidate.EntryPrice,
            outlayPhp,
            candidate.TurnoverPhp,
            candidate.AtrPct);

        return decimal.Round(outlayPhp * rate, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The pod-shop defensive curve: full size under a 5% drawdown, a linear cut to half size
    /// between 5% and 10%, and no new risk once the book is 10% below its peak.
    /// </summary>
    private static decimal ComputeDrawdownRiskMultiplier(decimal drawdownPct)
    {
        if (drawdownPct >= HardDrawdownThresholdPct)
        {
            return 0m;
        }

        if (drawdownPct <= SoftDrawdownThresholdPct)
        {
            return 1m;
        }

        var span = HardDrawdownThresholdPct - SoftDrawdownThresholdPct;
        var decayed = 1m - (((drawdownPct - SoftDrawdownThresholdPct) / span) * (1m - MinDrawdownRiskMultiplier));

        return decimal.Round(
            Math.Clamp(decayed, MinDrawdownRiskMultiplier, 1m),
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Causal EMA20 per ticker per session: each value is seeded from that ticker's own
    /// history up to and including that bar, so the trail can never peek forward.
    /// </summary>
    private static Dictionary<(string Ticker, DateOnly Date), decimal> BuildEma20Series(
        IReadOnlyList<StockMarketData> stocks)
    {
        var series = new Dictionary<(string, DateOnly), decimal>();

        foreach (var stock in stocks)
        {
            var closes = new List<decimal>(stock.Bars.Count);

            foreach (var bar in stock.Bars.OrderBy(bar => bar.Date))
            {
                closes.Add(bar.Close);

                var ema = QuantitativeFilterService.ComputeEma(closes);
                if (ema is decimal value && value > 0m)
                {
                    series[(stock.Ticker, bar.Date)] = value;
                }
            }
        }

        return series;
    }

    /// <summary>
    /// The mechanical exit ladder, evaluated on the closed bar only: at +1R the stop ratchets
    /// to the fee-paid break-even tick, and at +1.5R it trails one EMA20 bar behind. Both
    /// moves are one-way — the stop never loosens — and neither may cross the target, which
    /// would otherwise turn a winner into a same-bar stop.
    /// </summary>
    private static void RatchetStop(
        SimulatedPosition open,
        OhlcvBar session,
        IReadOnlyDictionary<(string Ticker, DateOnly Date), decimal> ema20ByTickerDate,
        DateOnly date)
    {
        if (open.NetRiskPhp <= 0m)
        {
            return;
        }

        var markProceedsPhp = PseBoardLotService.ComputeSellNetProceedsPhp(open.Shares, session.Close);
        var unrealizedR = (markProceedsPhp - open.BuyCostPhp) / open.NetRiskPhp;

        if (unrealizedR >= 1.0m
            && open.CurrentStopLoss < open.BreakEvenStopPrice
            && open.BreakEvenStopPrice < open.TargetPrice)
        {
            open.CurrentStopLoss = open.BreakEvenStopPrice;
            open.StopMode = StopModes.Breakeven;
        }

        if (unrealizedR < 1.5m)
        {
            return;
        }

        if (!ema20ByTickerDate.TryGetValue((open.Ticker, date), out var ema20))
        {
            return;
        }

        var trailedStop = PseBoardLotService.RoundToValidTick(ema20);
        if (trailedStop > open.CurrentStopLoss && trailedStop < open.TargetPrice)
        {
            open.CurrentStopLoss = trailedStop;
            open.StopMode = StopModes.TrailEma20;
        }
    }

    /// <summary>Aggregates the replay into the scorecard shape the UI already renders.</summary>
    private static BacktestResultDto BuildResult(
        HorizonProfile profile,
        IReadOnlyList<BacktestTradeDto> trades,
        IReadOnlyList<BacktestEquityPointDto> curve,
        decimal feesPaidPhp,
        decimal slippagePhp,
        decimal maxDrawdownPct,
        int sessions,
        DateOnly startDate,
        DateOnly endDate)
    {
        var endingEquityPhp = curve.Count > 0 ? curve[^1].EquityPhp : StartingCapitalPhp;
        var netReturnPct = decimal.Round((endingEquityPhp - StartingCapitalPhp) / StartingCapitalPhp * 100m, 2, MidpointRounding.AwayFromZero);
        var wins = trades.Count(trade => trade.NetPnlPhp > 0m);

        var winRate = trades.Count == 0
            ? 0m
            : decimal.Round((decimal)wins / trades.Count, 4, MidpointRounding.AwayFromZero);

        var expectancyR = trades.Count == 0
            ? 0m
            : decimal.Round(trades.Average(trade => trade.NetR), 4, MidpointRounding.AwayFromZero);

        var grossWin = trades.Where(trade => trade.NetR > 0m).Sum(trade => trade.NetR);
        var grossLoss = Math.Abs(trades.Where(trade => trade.NetR < 0m).Sum(trade => trade.NetR));

        var profitFactor = trades.Count == 0
            ? 0m
            : grossLoss == 0m
                ? (grossWin > 0m ? 99m : 0m)
                : decimal.Round(grossWin / grossLoss, 2, MidpointRounding.AwayFromZero);

        // The benchmark is the same capital held in the index from the first replayed session,
        // and alpha is measured after the desk pays its slippage: beating a frictionless
        // benchmark while paying friction is the only comparison that proves an edge.
        var benchmarkReturnPct = curve.Count > 0 && curve[^1].BenchmarkEquityPhp is decimal benchmarkEnd
            ? decimal.Round(
                (benchmarkEnd - StartingCapitalPhp) / StartingCapitalPhp * 100m,
                2,
                MidpointRounding.AwayFromZero)
            : (decimal?)null;

        // Slippage is already inside the equity curve (it is debited when a clip is opened), so the
        // net return and the after-slippage return are now the same number. The figure is kept as
        // its own field because the API and the UI report the friction as a cost line.
        var netReturnAfterSlippagePct = netReturnPct;

        var excessReturnPct = benchmarkReturnPct is decimal benchmark
            ? decimal.Round(netReturnAfterSlippagePct - benchmark, 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        var calendarDays = Math.Max(1, endDate.DayNumber - startDate.DayNumber);
        var growthFactor = (decimal)Math.Pow((double)(endingEquityPhp / StartingCapitalPhp), 365d / calendarDays);
        var cagrPct = decimal.Round((growthFactor - 1m) * 100m, 2, MidpointRounding.AwayFromZero);

        var sharpeRatio = ComputeAnnualizedSharpe(curve);
        var calmarRatio = maxDrawdownPct > 0m
            ? decimal.Round(cagrPct / maxDrawdownPct, 2, MidpointRounding.AwayFromZero)
            : (decimal?)null;

        return new BacktestResultDto(
            profile.Horizon.ToString(),
            startDate,
            endDate,
            StartingCapitalPhp,
            endingEquityPhp,
            netReturnPct,
            maxDrawdownPct,
            trades.Count,
            winRate,
            expectancyR,
            profitFactor,
            feesPaidPhp,
            sessions,
            curve,
            trades,
            benchmarkReturnPct,
            excessReturnPct,
            cagrPct,
            sharpeRatio,
            calmarRatio,
            slippagePhp,
            netReturnAfterSlippagePct);
    }

    /// <summary>
    /// Annualised Sharpe of the daily curve returns with a zero risk-free rate. Under 20 daily
    /// observations a sample standard deviation is noise, so the ratio is withheld instead.
    /// </summary>
    private static decimal? ComputeAnnualizedSharpe(IReadOnlyList<BacktestEquityPointDto> curve)
    {
        var returns = new List<double>(curve.Count);

        for (var index = 1; index < curve.Count; index++)
        {
            var previous = curve[index - 1].EquityPhp;
            if (previous <= 0m)
            {
                continue;
            }

            returns.Add((double)((curve[index].EquityPhp - previous) / previous));
        }

        if (returns.Count < MinimumSharpeSessions)
        {
            return null;
        }

        var mean = returns.Average();
        var variance = returns.Sum(value => (value - mean) * (value - mean)) / (returns.Count - 1);
        if (variance <= 0d)
        {
            return null;
        }

        var annualised = mean / Math.Sqrt(variance) * Math.Sqrt(TradingSessionsPerYear);

        return decimal.Round((decimal)annualised, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>One session's qualifying setup: the tick-rounded plan plus its rank inputs.</summary>
    private sealed record BacktestCandidate(
        string Ticker,
        decimal EntryPrice,
        decimal StopLoss,
        decimal TargetPrice,
        int Score,
        decimal TurnoverPhp,
        decimal? AtrPct);

    /// <summary>The three stops the ladder can be sitting on, and the exit reason they publish.</summary>
    private static class StopModes
    {
        public const string Initial = "INITIAL";
        public const string Breakeven = "BREAKEVEN";
        public const string TrailEma20 = "TRAIL_EMA20";
    }

    /// <summary>One open simulation position: the account state a real blotter would carry.</summary>
    private sealed class SimulatedPosition
    {
        public required string Ticker { get; init; }

        public required DateOnly EntryDate { get; init; }

        public required decimal EntryPrice { get; init; }

        public required decimal InitialStopLoss { get; init; }

        public required decimal TargetPrice { get; init; }

        public required decimal BreakEvenStopPrice { get; init; }

        public required decimal BuyCostPhp { get; init; }

        /// <summary>Round-trip slippage charged to the book when the clip was opened.</summary>
        public decimal SlippagePhp { get; init; }

        public required int Shares { get; init; }

        public required decimal NetRiskPhp { get; init; }

        /// <summary>The live stop: the initial stop, the break-even tick, or the EMA20 trail.</summary>
        public required decimal CurrentStopLoss { get; set; }

        public required string StopMode { get; set; }

        public int BarsHeld { get; set; }
    }
}
