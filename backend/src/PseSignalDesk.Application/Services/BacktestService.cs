using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Causal walk-forward backtester. It replays the horizon's own rule set over the real PSE
/// tape with a shared account that behaves like the live desk: up to
/// <see cref="MaxOpenPositions"/> simultaneous positions, every clip sized off <b>equity</b>
/// (not the shrinking cash balance), the per-trade risk throttled by the pod-shop heat cap,
/// the per-sector name cap and the drawdown governor, whole board lots, the per-leg round trip
/// (0.295% to buy, 0.395% to sell), the tick-spread/market-impact slippage estimate charged, and
/// the mechanical exit ladder (+1R break-even, +1.5R EMA20 trail) managing every open trade.
/// <para>
/// Two further money-manager rules apply. A setup whose fee-paid expected value is not positive
/// — or which the edge engine would only watch — is not funded at all, and the probability used
/// for that decision is refreshed by a walk-forward refit of the logistic money-manager model
/// every <see cref="WalkForwardRefitSessions"/> sessions, using only trades whose forward window
/// had already closed by the refit date.
/// </para>
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

    /// <summary>Concurrent positions the replay's book may hold, so one name cannot consume the account.</summary>
    public const int MaxOpenPositions = 5;

    /// <summary>Open risk the whole book may carry, matching the live portfolio heat cap.</summary>
    public const decimal MaxPortfolioHeatPct = 6m;

    /// <summary>Names one sector may contribute, matching the live per-sector cap.</summary>
    public const int MaxPositionsPerSector = 2;

    /// <summary>Notional share of equity one sector may occupy.</summary>
    public const decimal MaxSectorExposurePct = 35m;

    /// <summary>Sessions between walk-forward refits of the money-manager model.</summary>
    public const int WalkForwardRefitSessions = 60;

    /// <summary>Resolved trades required before a walk-forward refit is trusted.</summary>
    public const int MinimumWalkForwardTrials = 40;

    /// <summary>Risk multiplier applied when the edge engine can only justify a small size.</summary>
    public const decimal SmallSizeRiskMultiplier = 0.50m;

    /// <summary>Quarter-Kelly stake treated as a normal-sized bet, as on the live order ticket.</summary>
    public const decimal KellyReferenceFraction = 0.02m;

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
    private readonly ProbabilisticEdgeService _edge;

    public BacktestService(
        IMarketDataProvider marketData,
        QuantitativeFilterService quantFilter,
        ProbabilisticEdgeService? edge = null)
    {
        _marketData = marketData;
        _quantFilter = quantFilter;
        _edge = edge ?? new ProbabilisticEdgeService();
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
            var bars = stock.Bars.OrderBy(bar => bar.Date).ToList();
            if (bars.Count <= minBars)
            {
                continue;
            }

            var prefix = new List<OhlcvBar>(bars.Count);
            var indexPrefix = new List<OhlcvBar>(indexBars.Count);
            var indexCloses = new List<decimal>(indexBars.Count);
            var indexCursor = 0;
            var ema20 = BuildCausalEma20(bars);

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

                var regime = ResolveIndexRegime(indexPrefix, indexCloses);

                // The regime gate is measured on the closed prefix only: the PSEi needs its
                // full 200 sessions before the desk trusts the average, and a bearish tape
                // (close under both SMA50 and SMA200) funds no new long.
                if (regime == MarketRegime.BEARISH)
                {
                    continue;
                }

                var metric = _quantFilter.EvaluateStock(
                    stock with { Bars = prefix },
                    bars[index].Date,
                    indexPrefix,
                    computeEmpiricalBaseRate: false,
                    // A replay may not read the live session's foreign print or a filing published
                    // after this bar's close.
                    includeLiveForeignFlow: false);

                if (metric is null || !IsEligible(metric, profile))
                {
                    continue;
                }

                var candidate = BuildCandidate(metric, profile, stock.Sector, regime, bars[index].Date);
                if (candidate is null)
                {
                    continue;
                }

                // The label is the desk's own graded outcome for this setup, and it is the only
                // thing that lets a walk-forward refit learn. A window that has not resolved yet
                // yields no label, so the candidate is left out instead of being half-labelled.
                var label = LabelOutcome(bars, index, ema20, candidate, profile, bars[index].Date);
                if (label is null)
                {
                    continue;
                }

                if (!byDate.TryGetValue(bars[index].Date, out var list))
                {
                    list = new List<BacktestCandidate>();
                    byDate[bars[index].Date] = list;
                }

                list.Add(candidate with
                {
                    EntryDate = bars[index].Date,
                    OutcomeDate = label.Value.ExitDate,
                    Won = label.Value.Won,
                    RealizedR = label.Value.RealizedR
                });
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
        if (!QuantitativeFilterService.MeetsLiquidityFloor(
                metric.AvgTurnover20dPhp,
                metric.MedianTurnover20dPhp,
                metric.ActiveTradingDays20d))
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

        if (metric.VolumeRatioVs20d < profile.MinimumVolumeRatio)
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

    /// <summary>
    /// Prices one qualifying session: the tick-rounded ATR plan, the friction-inclusive reward
    /// ratio, the all-in fee rate the desk would charge on a pod-shop clip, the factor snapshot
    /// the money-manager model learns from and the probability the structural engine would have
    /// published <b>before</b> any learned residual. Only a plan that still pays at least 1.2R
    /// after the round trip is worth replaying.
    /// </summary>
    private BacktestCandidate? BuildCandidate(
        StockDailyMetric metric,
        HorizonProfile profile,
        string? sector,
        MarketRegime? regime,
        DateOnly tradeDate)
    {
        if (metric.Atr14 <= 0m)
        {
            return null;
        }

        var plan = PseBoardLotService.NormalizeTradePlan(
            metric.ClosePrice,
            metric.ClosePrice - (profile.AtrStopMultiplier * metric.Atr14),
            metric.ClosePrice + (profile.AtrTargetMultiplier * metric.Atr14));

        // Point-in-time friction: the round-trip rate charged on this session's own date. Before
        // 1 July 2025 the sale carried 0.60% STT, not the 0.10% in force today, and pricing a 2023
        // trade with today's rate flatters every historical R.
        var feeRate = PseBoardLotService.ComputeEffectiveRoundTripFeeRate(
            plan.Entry,
            MoneyManagerTrainingService.DefaultPositionBudgetPhp,
            metric.AvgTurnover20dPhp,
            metric.AtrPct,
            tradeDate);

        var netRiskReward = PseBoardLotService.ComputeNetRiskReward(plan.Entry, plan.StopLoss, plan.Target, feeRate);
        if (netRiskReward < 1.2m)
        {
            return null;
        }

        var tickSpreadPct = PseBoardLotService.ComputeTickSpreadRate(plan.Entry) * 100m;
        var foreignRatio = metric.NetForeignTodayPhp is decimal netForeign && metric.AvgTurnover20dPhp > 0m
            ? netForeign / metric.AvgTurnover20dPhp
            : (decimal?)null;

        // Conviction is pinned to its neutral 50 and breadth is left unreported: a replay must
        // not price a session with a reading the live run would not have had at that moment.
        var prior = _edge.EvaluateSetup(new SetupEdgeRequest(
            plan.Entry,
            plan.StopLoss,
            plan.Target,
            ConvictionScore: 50m,
            regime ?? MarketRegime.NEUTRAL,
            profile,
            EmpiricalWinRate: null,
            RelativeStrength63d: metric.RelativeStrength63d,
            VolatilityContractionRatio: metric.VolatilityContractionRatio,
            WeinsteinStage: metric.WeinsteinStage,
            BreadthAboveSma50Pct: null,
            TrapFlags: PseDisclosureClassifier.DetectTrapFlags(
                MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson)),
            AtrPct: metric.AtrPct,
            TrailingDividendYieldPct: metric.TrailingDividendYieldPct,
            SectorQuadrant: null,
            TickSpreadPct: tickSpreadPct,
            UpDownVolumeRatio20d: metric.UpDownVolumeRatio20d,
            SlippageInclusiveFeeRate: feeRate,
            NetForeignTurnoverRatio: foreignRatio,
            Sector: metric.Sector)).WinProbability;

        return new BacktestCandidate(
            metric.Ticker,
            sector ?? metric.Sector ?? "PSE",
            metric.QuantPreScore,
            plan.Entry,
            plan.StopLoss,
            plan.Target,
            metric.AvgTurnover20dPhp,
            metric.AtrPct,
            feeRate,
            netRiskReward,
            prior,
            TrainedModelFeatures.FromMetric(metric));
    }

    /// <summary>
    /// The causal PSEi read on the closed prefix. Below 200 sessions the averages are not
    /// trustworthy and the session is treated as neutral, which leaves the gate open.
    /// </summary>
    private static MarketRegime? ResolveIndexRegime(
        IReadOnlyList<OhlcvBar> indexPrefix,
        IReadOnlyList<decimal> indexCloses)
    {
        if (indexPrefix.Count < RegimeBarRequirement || indexCloses.Count < RegimeBarRequirement)
        {
            return null;
        }

        var sma50 = QuantitativeFilterService.ComputeSma(indexCloses, 50);
        var sma200 = QuantitativeFilterService.ComputeSma(indexCloses, RegimeBarRequirement);
        if (sma50 is not decimal fast || sma200 is not decimal slow)
        {
            return null;
        }

        var close = indexPrefix[^1].Close;

        return close >= fast && close >= slow
            ? MarketRegime.BULLISH
            : close < fast && close < slow
                ? MarketRegime.BEARISH
                : MarketRegime.NEUTRAL;
    }

    /// <summary>
    /// Causal EMA20 per bar, built incrementally from the initial 20-session SMA seed so the
    /// value on a date uses that date's close and nothing after it.
    /// </summary>
    private static decimal?[] BuildCausalEma20(IReadOnlyList<OhlcvBar> bars)
    {
        var series = new decimal?[bars.Count];
        var period = QuantitativeFilterService.EmaPeriod;
        var multiplier = 2m / (period + 1m);
        var seed = 0m;
        decimal? ema = null;

        for (var index = 0; index < bars.Count; index++)
        {
            seed += bars[index].Close;

            if (index < period - 1)
            {
                continue;
            }

            if (index == period - 1)
            {
                ema = seed / period;
            }
            else
            {
                ema = (bars[index].Close * multiplier) + (ema!.Value * (1m - multiplier));
            }

            series[index] = ema;
        }

        return series;
    }

    /// <summary>
    /// Grades one simulated session with the desk's own ladder — tick-grid plan, friction-paid
    /// barriers, the <c>+1R</c> break-even ratchet and the <c>+1.5R</c> EMA20 trail — so the
    /// walk-forward refit learns from the same money the replay would have made. Null when the
    /// forward window has not fully resolved yet.
    /// </summary>
    private static (DateOnly ExitDate, bool Won, decimal RealizedR)? LabelOutcome(
        IReadOnlyList<OhlcvBar> bars,
        int entryIndex,
        IReadOnlyList<decimal?> ema20,
        BacktestCandidate candidate,
        HorizonProfile profile,
        DateOnly entryDate)
    {
        var netRiskPerShare = PseBoardLotService.ComputeNetRiskPerShare(
            candidate.EntryPrice,
            candidate.StopLoss,
            candidate.FeeRate);

        if (netRiskPerShare <= 0m)
        {
            return null;
        }

        // The label pays the tax schedule that was in force when the clip was opened, not today's.
        var (buyRate, sellRate) = PseBoardLotService.SplitRoundTripFee(candidate.FeeRate, entryDate);
        var breakEvenStop = PseBoardLotService.ComputeBreakEvenStopPrice(candidate.EntryPrice);
        var stop = candidate.StopLoss;

        decimal NetR(decimal exit) =>
            decimal.Round(
                ((exit * (1m - sellRate)) - (candidate.EntryPrice * (1m + buyRate))) / netRiskPerShare,
                4,
                MidpointRounding.AwayFromZero);

        for (var offset = 1; offset <= profile.ForwardWindowBars; offset++)
        {
            var index = entryIndex + offset;
            if (index >= bars.Count)
            {
                return null;
            }

            var bar = bars[index];

            // A session that opens through the stop fills at the open, which is where a resting
            // stop order would actually have been executed; grading it at the stop would pay the
            // label a price the tape never offered.
            if (PseBoardLotService.ResolveStopFillPrice(stop, bar.Open, bar.Low) is decimal stopFill)
            {
                var stopR = NetR(stopFill);
                return (bar.Date, stopR > 0m, stopR);
            }

            if (bar.High >= candidate.TargetPrice)
            {
                return (bar.Date, true, NetR(candidate.TargetPrice));
            }

            if (offset == profile.ForwardWindowBars)
            {
                var expiryR = NetR(bar.Close);
                return (bar.Date, expiryR > 0m, expiryR);
            }

            var unrealizedR = NetR(bar.Close);

            if (unrealizedR >= 1.0m && stop < breakEvenStop && breakEvenStop < candidate.TargetPrice)
            {
                stop = breakEvenStop;
            }

            if (unrealizedR < 1.5m || index >= ema20.Count || ema20[index] is not decimal ema)
            {
                continue;
            }

            var trailedStop = PseBoardLotService.RoundToValidTick(ema);
            if (trailedStop > stop && trailedStop < candidate.TargetPrice)
            {
                stop = trailedStop;
            }
        }

        return null;
    }
    /// <summary>
    /// The multi-slot money-manager replay. Up to <see cref="MaxOpenPositions"/> names are held at
    /// once and each clip is sized off <b>equity</b> — the cash balance is only a funding check —
    /// so a book that is fully invested still sizes the next setup the way the live ticket would.
    /// New risk is throttled by the portfolio heat cap, the per-sector name cap and the sector
    /// exposure cap, and a setup is funded only when its fee-paid expected value is positive and
    /// the edge engine is willing to trade it. Exits fill on the stop, the target or the horizon's
    /// time stop; a bar that touched both barriers fills the stop.
    /// </summary>
    private BacktestResultDto Simulate(
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
        var open = new List<SimulatedPosition>();

        // The replay's own walk-forward model: refitted every WalkForwardRefitSessions sessions
        // on the candidates whose forward window had already resolved by then.
        var walkForward = new WalkForwardEdge();
        var seen = new List<BacktestCandidate>();
        var sessionsSinceRefit = WalkForwardRefitSessions;

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
            sessionsSinceRefit++;
            firstDate ??= date;
            lastDate = date;

            // Every candidate seen on an earlier session belongs to the walk-forward training
            // set once its forward window has closed. The refit therefore never sees a label it
            // could not have known, and appending a future bar cannot rewrite a past fit.
            if (candidatesByDate.TryGetValue(date, out var todayCandidates))
            {
                seen.AddRange(todayCandidates);
            }

            if (sessionsSinceRefit >= WalkForwardRefitSessions)
            {
                walkForward.Refit(seen.Where(candidate => candidate.OutcomeDate < date).ToList());
                sessionsSinceRefit = 0;
            }

            for (var slot = open.Count - 1; slot >= 0; slot--)
            {
                var position = open[slot];

                if (!barsByTicker[position.Ticker].TryGetValue(date, out var session))
                {
                    continue;
                }

                position.BarsHeld++;
                decimal? exitPrice = null;
                var exitReason = "TIME";

                if (PseBoardLotService.ResolveStopFillPrice(position.CurrentStopLoss, session.Open, session.Low)
                    is decimal stopFill)
                {
                    exitPrice = stopFill;
                    exitReason = position.StopMode == StopModes.Initial ? "STOP" : position.StopMode;
                }
                else if (session.High >= position.TargetPrice)
                {
                    exitPrice = position.TargetPrice;
                    exitReason = "TARGET";
                }
                else if (position.BarsHeld >= profile.ForwardWindowBars)
                {
                    exitPrice = session.Close;
                }

                if (exitPrice is not decimal exit)
                {
                    RatchetStop(position, session, ema20ByTickerDate, date);
                    continue;
                }

                // Both legs settle at the tax schedule of their own trade date: the sale carries
                // 0.60% STT before 1 July 2025 and 0.10% after, and the exit may be years after
                // the entry.
                cash += PseBoardLotService.ComputeSellNetProceedsPhp(position.Shares, exit, date);
                feesPaidPhp += ComputeFeesPaidPhp(position.Shares, position.EntryPrice, exit, position.EntryDate, date);

                // The clip's slippage was debited from cash at entry; subtract it from the
                // trade's own P&L as well so the per-trade R is a net number.
                var realizedPhp = PseBoardLotService.ComputeRealizedNetPnlPhp(
                                       position.Shares,
                                       position.EntryPrice,
                                       exit,
                                       position.EntryDate,
                                       date)
                                   - position.SlippagePhp;
                trades.Add(new BacktestTradeDto(
                    position.Ticker,
                    position.EntryDate,
                    position.EntryPrice,
                    date,
                    exit,
                    position.Shares,
                    realizedPhp,
                    position.NetRiskPhp > 0m
                        ? decimal.Round(realizedPhp / position.NetRiskPhp, 4, MidpointRounding.AwayFromZero)
                        : 0m,
                    exitReason,
                    position.BarsHeld,
                    position.SlippagePhp));

                open.RemoveAt(slot);
            }

            // Mark the book before sizing: the heat cap, position cap and sector cap are all
            // fractions of equity, not of the cash balance.
            var openValuePhp = open.Sum(position =>
                lastClose.TryGetValue(position.Ticker, out var mark)
                    ? PseBoardLotService.ComputeSellNetProceedsPhp(position.Shares, mark, date)
                    : 0m);

            var equityPhp = decimal.Round(cash + openValuePhp, 2, MidpointRounding.AwayFromZero);

            if (candidatesByDate.TryGetValue(date, out var candidates) && candidates.Count > 0)
            {
                // Pod-shop governor: new risk is cut on the way down the equity curve, so a
                // soft 5% drawdown halves the clip and a 10% drawdown stops funding trades.
                var drawdownPct = peakPhp > 0m
                    ? (peakPhp - lastEquityPhp) / peakPhp * 100m
                    : 0m;

                var drawdownMultiplier = ComputeDrawdownRiskMultiplier(drawdownPct);
                var openRiskPhp = open.Sum(position => position.NetRiskPhp);

                foreach (var candidate in candidates
                             .OrderByDescending(entry => entry.Score)
                             .ThenBy(entry => entry.Ticker, StringComparer.Ordinal))
                {
                    if (open.Count >= MaxOpenPositions)
                    {
                        break;
                    }

                    if (open.Any(position => string.Equals(
                            position.Ticker, candidate.Ticker, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    if (open.Count(position => string.Equals(
                            position.Sector, candidate.Sector, StringComparison.OrdinalIgnoreCase)) >=
                        MaxPositionsPerSector)
                    {
                        continue;
                    }

                    var sectorExposurePhp = open
                        .Where(position => string.Equals(
                            position.Sector, candidate.Sector, StringComparison.OrdinalIgnoreCase))
                        .Sum(position => position.BuyCostPhp);

                    // A new clip could push the sector past its ceiling; skip it instead of
                    // discovering the breach after the fact.
                    if (equityPhp > 0m &&
                        ((sectorExposurePhp / equityPhp * 100m) + MaxPositionPct) > MaxSectorExposurePct)
                    {
                        continue;
                    }

                    var probability = walkForward.Probability(candidate);
                    var expectedValueR = PseBoardLotService.ComputeExpectedValueR(
                        probability,
                        candidate.NetRiskReward);

                    // The money-manager gate: a setup whose fee-paid expectancy is not positive is
                    // not funded at all, and a marginal one is only paper-traded at half risk.
                    if (expectedValueR <= 0m)
                    {
                        continue;
                    }

                    var verdictMultiplier = expectedValueR >= 0.25m ? 1m : SmallSizeRiskMultiplier;
                    var kellyMultiplier = walkForward.KellyMultiplier(probability, candidate.NetRiskReward);
                    var riskFraction = RiskPerTradePct / 100m * drawdownMultiplier * verdictMultiplier * kellyMultiplier;

                    var netRiskPerShare = PseBoardLotService.ComputeNetRiskPerShare(
                        candidate.EntryPrice,
                        candidate.StopLoss,
                        candidate.FeeRate);

                    if (netRiskPerShare <= 0m)
                    {
                        continue;
                    }

                    // Portfolio heat: the whole book may not carry more open risk than the cap, so
                    // the room left — not the per-trade budget — is what funds the next clip.
                    var heatRoomPhp = (equityPhp * (MaxPortfolioHeatPct / 100m)) - openRiskPhp;
                    if (heatRoomPhp <= 0m)
                    {
                        continue;
                    }

                    var budgetPhp = Math.Min(equityPhp * riskFraction, heatRoomPhp);
                    var sized = PseBoardLotService.SizePositionByRiskBudget(
                        equityPhp,
                        equityPhp > 0m ? budgetPhp / equityPhp : 0m,
                        candidate.EntryPrice,
                        candidate.StopLoss,
                        candidate.TargetPrice,
                        candidate.TurnoverPhp,
                        MaxPositionPct / 100m,
                        roundTripFeeRate: candidate.FeeRate);

                    var costPhp = PseBoardLotService.ComputeBuyTotalCostPhp(sized.Shares, candidate.EntryPrice, date);

                    // Slippage is a real cash cost of the clip (tick spread plus impact on both legs),
                    // so it is debited from the book at entry instead of being deducted from the
                    // reported return at the end: the equity curve, the drawdown governor and the
                    // per-trade R must all see the same money.
                    var slippageCostPhp = ComputeSlippageCostPhp(costPhp, candidate);

                    // Cash is still the funding check: an equity-derived size that the free cash
                    // cannot settle is cut back to the whole lots it can actually pay for.
                    if (sized.Shares > 0 && costPhp > 0m && costPhp + slippageCostPhp > cash)
                    {
                        var boardLot = PseBoardLotService.GetBoardLotSize(candidate.EntryPrice);
                        var slippageRate = PseBoardLotService.ComputeEstimatedSlippageRate(
                            candidate.EntryPrice,
                            cash,
                            candidate.TurnoverPhp,
                            candidate.AtrPct);
                        var allInPerShare = candidate.EntryPrice
                                            * (1m + PseBoardLotService.BuyLegFeeRate)
                                            * (1m + slippageRate);

                        if (boardLot <= 0 || allInPerShare <= 0m)
                        {
                            continue;
                        }

                        var affordableLots = (long)Math.Floor(cash / allInPerShare);
                        var affordableShares = (int)Math.Min(
                            (affordableLots / boardLot) * boardLot,
                            int.MaxValue);

                        costPhp = PseBoardLotService.ComputeBuyTotalCostPhp(affordableShares, candidate.EntryPrice, date);
                        slippageCostPhp = ComputeSlippageCostPhp(costPhp, candidate);
                        sized = sized with { Shares = affordableShares };
                    }

                    // A clip the free cash cannot settle — fees and slippage included — is not an
                    // order the desk could have placed, so it is dropped rather than overdrawn.
                    if (sized.Shares <= 0 || costPhp <= 0m || costPhp + slippageCostPhp > cash)
                    {
                        continue;
                    }

                    var netRiskPhp = PseBoardLotService.ComputeInitialNetRiskPhp(
                        sized.Shares,
                        candidate.EntryPrice,
                        candidate.StopLoss,
                        date);

                    if (netRiskPhp <= 0m)
                    {
                        continue;
                    }

                    cash -= costPhp;
                    cash -= slippageCostPhp;
                    slippagePhp += slippageCostPhp;
                    openRiskPhp += netRiskPhp;

                    open.Add(new SimulatedPosition
                    {
                        Ticker = candidate.Ticker,
                        Sector = candidate.Sector,
                        EntryDate = date,
                        EntryPrice = candidate.EntryPrice,
                        InitialStopLoss = candidate.StopLoss,
                        CurrentStopLoss = candidate.StopLoss,
                        TargetPrice = candidate.TargetPrice,
                        BreakEvenStopPrice = PseBoardLotService.ComputeBreakEvenStopPrice(candidate.EntryPrice),
                        StopMode = StopModes.Initial,
                        BuyCostPhp = costPhp,
                        SlippagePhp = slippageCostPhp,
                        Shares = sized.Shares,
                        NetRiskPhp = netRiskPhp
                    });
                }
            }

            // The curve is marked again after the entries, so it includes the cash that just left
            // the book and the value of the clips that replaced it.
            openValuePhp = open.Sum(position =>
                lastClose.TryGetValue(position.Ticker, out var mark)
                    ? PseBoardLotService.ComputeSellNetProceedsPhp(position.Shares, mark, date)
                    : 0m);

            equityPhp = decimal.Round(cash + openValuePhp, 2, MidpointRounding.AwayFromZero);
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

    private static decimal ComputeFeesPaidPhp(
        int shares,
        decimal entryPrice,
        decimal exitPrice,
        DateOnly entryDate,
        DateOnly exitDate)
    {
        var buyLeg = PseBoardLotService.ComputeLegFeePhp(shares, entryPrice, isSell: false, entryDate);
        var sellLeg = PseBoardLotService.ComputeLegFeePhp(shares, exitPrice, isSell: true, exitDate);

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

        var markProceedsPhp = PseBoardLotService.ComputeSellNetProceedsPhp(open.Shares, session.Close, session.Date);
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

    /// <summary>
    /// One session's qualifying setup: the tick-rounded plan, the friction it must carry, the
    /// structural probability it would have been published at, the factor snapshot the learned
    /// model consumes and — once the forward window closes — its graded label.
    /// </summary>
    private sealed record BacktestCandidate(
        string Ticker,
        string Sector,
        int Score,
        decimal EntryPrice,
        decimal StopLoss,
        decimal TargetPrice,
        decimal TurnoverPhp,
        decimal? AtrPct,
        decimal FeeRate,
        decimal NetRiskReward,
        decimal PriorProbability,
        decimal?[] Features,
        DateOnly EntryDate = default,
        DateOnly OutcomeDate = default,
        bool Won = false,
        decimal RealizedR = 0m);

    /// <summary>
    /// The replay's own money-manager model. It is refitted walk-forward on the candidates whose
    /// forward windows had already closed, on the same residual-over-the-prior geometry the
    /// offline trainer uses (<c>logit(prior)</c> as a per-trial offset), and it is only put to work
    /// when it beats the neutral base rate on those resolved trades. Because a replay has no
    /// separate held-out slice, the L2 strength is fixed rather than scanned and the acceptance
    /// test is in-sample: this model can sharpen the sizing inside a replay, but promotion to
    /// live pricing remains the offline trainer's decision.
    /// </summary>
    private sealed class WalkForwardEdge
    {
        private const double FixedLambda = 25d;

        private LearnedEdgeModel? _model;

        /// <summary>The probability this session is priced at: the prior, tilted by the learned residual.</summary>
        public decimal Probability(BacktestCandidate candidate)
        {
            if (_model is null)
            {
                return candidate.PriorProbability;
            }

            var prior = Math.Clamp(candidate.PriorProbability, 0.01m, 0.99m);
            var tilted = prior + _model.ResidualTilt(prior, candidate.Features);

            return decimal.Round(Math.Clamp(tilted, 0.01m, 0.99m), 6, MidpointRounding.AwayFromZero);
        }

        /// <summary>
        /// The live governor's edge dial, applied to the replay: a quarter-Kelly stake of 2% is a
        /// normal clip, half that is halved and a 3%+ stake is capped at 1.5x.
        /// </summary>
        public decimal KellyMultiplier(decimal probability, decimal netRiskReward)
        {
            var quarterKelly = PseBoardLotService.ComputeQuarterKellyFraction(probability, netRiskReward);

            return decimal.Round(
                Math.Clamp(quarterKelly / KellyReferenceFraction, 0.50m, 1.50m),
                2,
                MidpointRounding.AwayFromZero);
        }

        /// <summary>Refits on the resolved trades. Too few samples leaves the prior in charge.</summary>
        public void Refit(IReadOnlyList<BacktestCandidate> resolved)
        {
            if (resolved.Count < MinimumWalkForwardTrials)
            {
                return;
            }

            var dimension = TrainedModelFeatures.Length;
            var means = new double[dimension];
            var scales = new double[dimension];

            for (var column = 0; column < dimension; column++)
            {
                var values = resolved
                    .Select(candidate => candidate.Features[column])
                    .Where(value => value.HasValue)
                    .Select(value => (double)value!.Value)
                    .ToArray();

                if (values.Length == 0)
                {
                    means[column] = 0d;
                    scales[column] = 1d;
                    continue;
                }

                var mean = values.Average();
                var variance = values.Sum(value => (value - mean) * (value - mean)) / Math.Max(1, values.Length - 1);
                var deviation = Math.Sqrt(variance);

                means[column] = mean;
                scales[column] = deviation > 1e-9d ? deviation : 1d;
            }

            var x = resolved.Select(candidate => Standardize(candidate.Features, means, scales)).ToArray();
            var y = resolved.Select(candidate => candidate.Won ? 1d : 0d).ToArray();
            var offsets = resolved
                .Select(candidate => LogisticModelTrainer.Logit(
                    (double)Math.Clamp(candidate.PriorProbability, 0.01m, 0.99m)))
                .ToArray();

            var fitted = LogisticModelTrainer.Fit(
                x,
                y,
                FixedLambda,
                sampleWeights: ClusterWeights(resolved),
                offsets: offsets);

            var baseRate = Math.Clamp(y.Average(), 0.02d, 0.98d);
            var baselineBrier = y.Average(label => (baseRate - label) * (baseRate - label));
            var probabilities = x
                .Select((row, index) => LogisticModelTrainer.Predict(fitted, row, offsets[index]))
                .ToArray();
            var trainedBrier = probabilities
                .Select((probability, index) => (probability - y[index]) * (probability - y[index]))
                .Average();

            if (trainedBrier >= baselineBrier)
            {
                _model = null;
                return;
            }

            var weights = new Dictionary<string, double>(StringComparer.Ordinal);
            var stats = new Dictionary<string, FeatureStat>(StringComparer.Ordinal);

            for (var index = 0; index < dimension; index++)
            {
                weights[TrainedModelFeatures.Keys[index]] = fitted.Weights[index];
                stats[TrainedModelFeatures.Keys[index]] = new FeatureStat(means[index], scales[index]);
            }

            _model = new LearnedEdgeModel(
                "BACKTEST",
                fitted.Intercept,
                weights,
                stats,
                LearnedEdgeModel.NeutralKellyMultiplier);
        }

        /// <summary>One market-wide session must not outvote the idiosyncratic setups beside it.</summary>
        private static double[] ClusterWeights(IReadOnlyList<BacktestCandidate> resolved)
        {
            var perDate = resolved
                .GroupBy(candidate => candidate.EntryDate)
                .ToDictionary(group => group.Key, group => group.Count());

            var weights = resolved
                .Select(candidate => 1d / Math.Sqrt(perDate[candidate.EntryDate]))
                .ToArray();

            var mean = weights.Average();

            if (mean <= 0d)
            {
                return weights.Select(_ => 1d).ToArray();
            }

            for (var index = 0; index < weights.Length; index++)
            {
                weights[index] /= mean;
            }

            return weights;
        }

        private static double[] Standardize(decimal?[] features, double[] means, double[] scales)
        {
            var standardized = new double[means.Length];

            for (var column = 0; column < means.Length && column < features.Length; column++)
            {
                // A factor the session did not report is mean-imputed, which is exactly zero after
                // standardization, so it contributes nothing instead of inventing a level.
                standardized[column] = features[column] is decimal value && scales[column] > 0d
                    ? ((double)value - means[column]) / scales[column]
                    : 0d;
            }

            return standardized;
        }
    }

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

        /// <summary>The sector cap this position occupies, so a second name in it is refused.</summary>
        public required string Sector { get; init; }

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
