using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// The desk's self-training layer. It replays the real PSE tape, labels every qualifying
/// session with the horizon's own ATR-barrier outcome <b>priced the way the desk actually
/// trades</b> (tick grid, round-trip slippage, the +1R break-even / +1.5R EMA20 ladder and a
/// causal PSEi bearish gate), fits the twelve scale-free factor weights on the first 70% of the
/// labelled trades as a <b>residual</b> over the structural prior, purges and embargoes the
/// overlap with the forward windows, and then grades the fit on the last 30% it never saw. A
/// model is promoted to live pricing only when the held-out sample is large enough, its Brier
/// score and log loss beat the neutral baseline, and the trades it would actually have funded
/// carry positive expectancy; otherwise the row is stored inactive so the terminal can explain
/// the fallback instead of hiding it.
/// </summary>
public sealed class MoneyManagerTrainingService
{
    /// <summary>Calendar history requested from the live feed. ~1,080 days is about four years of sessions.</summary>
    public const int DefaultLookbackCalendarDays = 1080;

    /// <summary>Below this many labelled trades no weight is allowed to price a live pick.</summary>
    public const int MinimumTrials = 80;

    /// <summary>Minimum held-out trades before a learned model can be promoted.</summary>
    public const int MinimumValidationTrials = 25;

    /// <summary>Share of the labelled trades kept for fitting; the rest is the held-out slice.</summary>
    public const decimal TrainFraction = 0.70m;

    /// <summary>How much worse than the neutral prior the held-out Brier may be before rejection.</summary>
    public const decimal ValidationBrierTolerance = 0.02m;

    /// <summary>The liquid universe the trainer may learn from. Same floor the live screen uses.</summary>
    public const decimal MinimumTurnoverPhp = 5_000_000m;

    /// <summary>Below this many in-sample trades the logistic fit is not attempted at all.</summary>
    public const int MinimumTrainingTrialsForFit = 20;

    /// <summary>Candidate L2 strengths, scanned on the held-out slice.</summary>
    public static readonly decimal[] LambdaGrid = { 0.5m, 2m, 8m, 25m, 60m };

    /// <summary>Candidate Kelly fractions of full Kelly, scanned for held-out log growth.</summary>
    public static readonly decimal[] KellyGrid = { 0.15m, 0.20m, 0.25m, 0.30m, 0.35m };

    /// <summary>Fallback stake multiplier when no held-out slice exists to search on.</summary>
    public const decimal NeutralKellyFraction = 0.25m;

    /// <summary>
    /// Notional the trainer assumes when it prices a simulated trade's round-trip slippage.
    /// A pod-shop clip, not a hero position: at this size the estimate is dominated by the
    /// tick spread, which is the friction that actually differs between a PHP 1 and a PHP 400 name.
    /// </summary>
    public const decimal DefaultPositionBudgetPhp = 50_000m;

    /// <summary>
    /// Hard ceiling on the equity fraction one simulated trade may risk when the held-out Kelly
    /// scan is scored. This is what keeps <see cref="OptimalKelly"/> in pod-shop units: a full
    /// Kelly fraction is a probability edge, the scan multiplies it by a candidate scalar and
    /// this cap keeps the product inside a defensible 0–2.5% of equity per trade instead of the
    /// 15–35% of bankroll the pre-fix compounding implied.
    /// </summary>
    public const decimal MaxHeldOutStakeFraction = 0.025m;

    /// <summary>Floor on a trial's magnitude weight, so a scratch still counts.</summary>
    public const decimal MinimumMagnitudeWeight = 0.50m;

    /// <summary>Ceiling on a trial's magnitude weight, so one monster winner cannot dominate.</summary>
    public const decimal MaximumMagnitudeWeight = 2.00m;

    private readonly IMarketDataProvider _marketData;
    private readonly QuantitativeFilterService _quantFilter;
    private readonly ISignalRepository _repository;
    private readonly ProbabilisticEdgeService _edge;
    private readonly ILogger<MoneyManagerTrainingService>? _logger;

    public MoneyManagerTrainingService(
        IMarketDataProvider marketData,
        QuantitativeFilterService quantFilter,
        ISignalRepository repository,
        ILogger<MoneyManagerTrainingService>? logger = null,
        ProbabilisticEdgeService? edge = null)
    {
        _marketData = marketData;
        _quantFilter = quantFilter;
        _repository = repository;
        _logger = logger;
        _edge = edge ?? new ProbabilisticEdgeService();
    }

    /// <summary>Fits and persists one horizon's model from a fresh live fetch.</summary>
    public async Task<TrainedModelSummaryDto> TrainAsync(
        SignalHorizon horizon,
        DateOnly? asOfDate = null,
        int? lookbackDays = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await FetchTrainingBatchAsync(asOfDate, lookbackDays, cancellationToken);
        var trials = BuildTrials(batch);
        var model = Fit(horizon, trials.TryGetValue(horizon, out var horizonTrials)
            ? horizonTrials
            : Array.Empty<TrainingTrial>());

        await _repository.SaveTrainedModelAsync(model, cancellationToken);
        LogFit(model);

        return TrainedModelPresenter.ToDto(model, horizon.ToString());
    }

    /// <summary>
    /// Fits all four horizons from one fetch. The per-bar metric pass is shared, so training
    /// the whole desk costs roughly one horizon's work instead of four.
    /// </summary>
    public async Task<IReadOnlyList<TrainedModelSummaryDto>> TrainAllAsync(
        DateOnly? asOfDate = null,
        int? lookbackDays = null,
        CancellationToken cancellationToken = default)
    {
        var batch = await FetchTrainingBatchAsync(asOfDate, lookbackDays, cancellationToken);
        var trials = BuildTrials(batch);
        var results = new List<TrainedModelSummaryDto>(ProbabilisticEdgeService.AllHorizons.Count);

        foreach (var horizon in ProbabilisticEdgeService.AllHorizons)
        {
            var model = Fit(horizon, trials.TryGetValue(horizon, out var horizonTrials)
                ? horizonTrials
                : Array.Empty<TrainingTrial>());

            await _repository.SaveTrainedModelAsync(model, cancellationToken);
            results.Add(TrainedModelPresenter.ToDto(model, horizon.ToString()));
            LogFit(model);
        }

        return results;
    }

    /// <summary>Reads the stored model for one horizon without retraining.</summary>
    public async Task<TrainedModelSummaryDto> GetModelAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default)
    {
        var model = await _repository.GetTrainedModelAsync(horizon, cancellationToken);
        return TrainedModelPresenter.ToDto(model, horizon.ToString());
    }

    /// <summary>
    /// Pulls a deep, live history for the trainer. A feed with no candles is a hard failure:
    /// the desk would rather answer 503 than fit weights on a synthetic tape.
    /// </summary>
    private async Task<MarketDataBatch> FetchTrainingBatchAsync(
        DateOnly? asOfDate,
        int? lookbackDays,
        CancellationToken cancellationToken)
    {
        var resolvedDate = asOfDate
                           ?? (await _repository.GetLatestSnapshotAsync(cancellationToken))?.TradeDate
                           ?? DateOnly.FromDateTime(DateTime.UtcNow);

        var calendarDays = Math.Max(
            QuantitativeFilterService.MinBarsRequired + 120,
            lookbackDays ?? DefaultLookbackCalendarDays);

        var batch = await _marketData.FetchAsync(resolvedDate, calendarDays, cancellationToken);

        if (batch.Stocks.Count == 0)
        {
            throw new MarketDataUnavailableException(
                $"No live PSE history is available through {resolvedDate:yyyy-MM-dd}, so the money-manager " +
                "model cannot be trained. No weights were invented.");
        }

        return batch;
    }

    /// <summary>
    /// Replays each liquid ticker bar by bar and labels every qualifying session with the
    /// horizon's own barrier outcome. Liquidity is judged <b>causally</b> on the 20-day turnover
    /// of the session being labelled, never on today's turnover: a name that was tradable in
    /// 2022 and has since dried up still belongs in the training set (and a name that only
    /// became liquid recently does not get four years of hindsight credit). Sessions on which
    /// the closed PSEi prefix was under both its 50- and 200-day average are skipped, because
    /// the live `TRADE` gate and the causal replay refuse to fund a long there either.
    /// One metric pass feeds all four horizons: only the eligibility gates, the barrier
    /// multiples and the forward window differ per book.
    /// </summary>
    private Dictionary<SignalHorizon, List<TrainingTrial>> BuildTrials(MarketDataBatch batch)
    {
        var result = ProbabilisticEdgeService.AllHorizons
            .ToDictionary(horizon => horizon, _ => new List<TrainingTrial>());
        var indexBars = batch.IndexBars ?? Array.Empty<OhlcvBar>();
        var indexRegime = CausalIndexRegime.Build(indexBars);
        var minBars = Math.Max(
            QuantitativeFilterService.MinBarsRequired,
            HorizonProfile.For(SignalHorizon.WEEK).MinHistoryBars);

        foreach (var stock in batch.Stocks)
        {
            var bars = stock.Bars.OrderBy(bar => bar.Date).ToList();
            if (bars.Count <= minBars)
            {
                continue;
            }

            var prefilter = PrefixPrefilter.Build(bars);
            var ema20 = BuildEma20Series(bars);
            var prefix = new List<OhlcvBar>(bars.Count);
            var indexPrefix = new List<OhlcvBar>(indexBars.Count);
            var indexCursor = 0;

            for (var index = 0; index < bars.Count; index++)
            {
                prefix.Add(bars[index]);

                while (indexCursor < indexBars.Count && indexBars[indexCursor].Date <= bars[index].Date)
                {
                    indexPrefix.Add(indexBars[indexCursor]);
                    indexCursor++;
                }

                if (index < minBars - 1 || !prefilter.Passes(index))
                {
                    continue;
                }

                // The same causal regime gate the live run and the replay apply: no long is
                // learned on a session the desk would not have funded.
                if (indexRegime.IsBearishAt(bars[index].Date))
                {
                    continue;
                }

                var metric = _quantFilter.EvaluateStock(
                    stock with { Bars = prefix },
                    bars[index].Date,
                    indexPrefix,
                    computeEmpiricalBaseRate: false);

                if (metric is null)
                {
                    continue;
                }

                var features = TrainedModelFeatures.FromMetric(metric);
                var regime = indexRegime.RegimeAt(bars[index].Date);

                foreach (var horizon in ProbabilisticEdgeService.AllHorizons)
                {
                    var profile = HorizonProfile.For(horizon);

                    if (index + profile.ForwardWindowBars >= bars.Count || !IsEligible(metric, profile))
                    {
                        continue;
                    }

                    var outcome = SimulateBarrier(bars, index, ema20, metric, profile);
                    if (outcome is null)
                    {
                        continue;
                    }

                    result[horizon].Add(new TrainingTrial(
                        stock.Ticker,
                        bars[index].Date,
                        outcome.Value.ExitDate,
                        outcome.Value.Won,
                        outcome.Value.RealizedR,
                        outcome.Value.NetRiskReward,
                        ComputeStructuralPriorProbability(metric, profile, outcome.Value, regime),
                        features));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The probability the desk's own structural engine would have published for this exact
    /// plan on this exact session, <b>before</b> any learned residual. It is the offset the
    /// logistic fit learns on top of, which is what stops the twelve factors from being counted
    /// twice (once by the heuristic prior, once by the weights).
    /// <para>
    /// Conviction is pinned to its neutral 50 — a training session has no model to be confident
    /// about — and the breadth dial stays unreported rather than being zero-filled, exactly like
    /// a live request that does not carry it.
    /// </para>
    /// </summary>
    private decimal ComputeStructuralPriorProbability(
        StockDailyMetric metric,
        HorizonProfile profile,
        TrainingOutcome outcome,
        MarketRegime? regime)
    {
        var disclosures = MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson);
        var trapFlags = PseDisclosureClassifier.DetectTrapFlags(disclosures);
        var foreignRatio = metric.NetForeignTodayPhp is decimal netForeign &&
                           metric.AvgTurnover20dPhp > 0m
            ? netForeign / metric.AvgTurnover20dPhp
            : (decimal?)null;
        var tickSpreadPct = outcome.PlanEntry > 0m
            ? PseBoardLotService.ComputeTickSpreadRate(outcome.PlanEntry) * 100m
            : 0m;

        return _edge.EvaluateSetup(new SetupEdgeRequest(
            outcome.PlanEntry,
            outcome.StopLoss,
            outcome.Target,
            ConvictionScore: 50m,
            regime ?? MarketRegime.NEUTRAL,
            profile,
            EmpiricalWinRate: null,
            RelativeStrength63d: metric.RelativeStrength63d,
            VolatilityContractionRatio: metric.VolatilityContractionRatio,
            WeinsteinStage: metric.WeinsteinStage,
            BreadthAboveSma50Pct: null,
            TrapFlags: trapFlags,
            AtrPct: metric.AtrPct,
            TrailingDividendYieldPct: metric.TrailingDividendYieldPct,
            SectorQuadrant: null,
            TickSpreadPct: tickSpreadPct,
            UpDownVolumeRatio20d: metric.UpDownVolumeRatio20d,
            SlippageInclusiveFeeRate: outcome.FeeRate,
            NetForeignTurnoverRatio: foreignRatio,
            Sector: metric.Sector)).WinProbability;
    }

    private static bool IsEligible(StockDailyMetric metric, HorizonProfile profile)
    {
        if (metric.AvgTurnover20dPhp < MinimumTurnoverPhp ||
            metric.ClosePrice <= metric.Sma50 ||
            metric.ClosePrice <= metric.Sma200)
        {
            return false;
        }

        if (metric.Rsi14 < profile.RsiIdealLow || metric.Rsi14 > Math.Max(profile.RsiIdealHigh, 78m))
        {
            return false;
        }

        if (metric.VolumeRatioVs20d < 1.2m ||
            metric.AtrPct is not decimal atrPct ||
            atrPct < 1.0m ||
            metric.WeinsteinStage is "STAGE_3" or "STAGE_4")
        {
            return false;
        }

        var flags = PseDisclosureClassifier.DetectTrapFlags(
            MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson));

        return !PseDisclosureClassifier.HasHardDisqualifier(flags);
    }

    /// <summary>
    /// Labels one session the way the desk would actually have traded it, not the way a
    /// frictionless textbook would:
    /// <list type="bullet">
    /// <item>the plan is landed on the PSE tick grid (and clamped inside the static band on
    /// <c>DAY</c>), so the simulated stop and target are orderable prices;</item>
    /// <item>the round-trip friction is the tick-spread plus square-root-impact estimate the
    /// order ticket charges, not just the statutory fee;</item>
    /// <item>the <c>+1R</c> break-even ratchet and the <c>+1.5R</c> causal EMA20 trail move the
    /// stop exactly like <c>BacktestService</c> and the blotter do, so a runner that later
    /// stalls is graded as a scratch instead of a full <c>-1R</c> loss.</item>
    /// </list>
    /// The stop is checked before the target on every bar (daily OHLC cannot order a
    /// same-session touch) and a window that expires unresolved is graded on the net close.
    /// </summary>
    private static TrainingOutcome? SimulateBarrier(
        IReadOnlyList<OhlcvBar> bars,
        int entryIndex,
        IReadOnlyList<decimal?> ema20,
        StockDailyMetric metric,
        HorizonProfile profile)
    {
        var atr = metric.Atr14;
        var rawEntry = PseBoardLotService.RoundToValidTick(metric.ClosePrice);

        if (rawEntry <= 0m || atr <= 0m)
        {
            return null;
        }

        var rawStop = rawEntry - (profile.AtrStopMultiplier * atr);
        var rawTarget = rawEntry + (profile.AtrTargetMultiplier * atr);
        var plan = profile.Horizon == SignalHorizon.DAY
            ? PseBoardLotService.NormalizeTradePlanWithinStaticLimits(rawEntry, rawEntry, rawStop, rawTarget)
            : PseBoardLotService.NormalizeTradePlan(rawEntry, rawStop, rawTarget);

        var feeRate = PseBoardLotService.ComputeEffectiveRoundTripFeeRate(
            plan.Entry,
            DefaultPositionBudgetPhp,
            metric.AvgTurnover20dPhp,
            metric.AtrPct);

        var netRisk = PseBoardLotService.ComputeNetRiskPerShare(plan.Entry, plan.StopLoss, feeRate);
        var netReward = PseBoardLotService.ComputeNetRewardPerShare(plan.Entry, plan.Target, feeRate);

        if (netRisk <= 0m || netReward <= 0m)
        {
            return null;
        }

        var netRiskReward = decimal.Round(netReward / netRisk, 2, MidpointRounding.AwayFromZero);
        var breakEvenStop = PseBoardLotService.ComputeBreakEvenStopPrice(plan.Entry);
        var (buyRate, sellRate) = PseBoardLotService.SplitRoundTripFee(feeRate);
        var stop = plan.StopLoss;

        for (var offset = 1; offset <= profile.ForwardWindowBars; offset++)
        {
            var bar = bars[entryIndex + offset];

            if (bar.Low <= stop)
            {
                var stopR = NetRealizedR(plan.Entry, stop, buyRate, sellRate, netRisk);

                return new TrainingOutcome(
                    bar.Date,
                    stopR > 0m,
                    stopR,
                    plan.Entry,
                    plan.StopLoss,
                    plan.Target,
                    netRiskReward,
                    feeRate);
            }

            if (bar.High >= plan.Target)
            {
                var targetR = NetRealizedR(plan.Entry, plan.Target, buyRate, sellRate, netRisk);

                return new TrainingOutcome(
                    bar.Date,
                    true,
                    targetR,
                    plan.Entry,
                    plan.StopLoss,
                    plan.Target,
                    netRiskReward,
                    feeRate);
            }

            if (offset == profile.ForwardWindowBars)
            {
                var expiryR = NetRealizedR(plan.Entry, bar.Close, buyRate, sellRate, netRisk);

                return new TrainingOutcome(
                    bar.Date,
                    expiryR > 0m,
                    expiryR,
                    plan.Entry,
                    plan.StopLoss,
                    plan.Target,
                    netRiskReward,
                    feeRate);
            }

            // The exit ladder is evaluated on the closed bar, after that bar's own barriers:
            // exactly the order the replay and the blotter use.
            RatchetTrainingStop(
                ref stop,
                plan,
                breakEvenStop,
                netRisk,
                buyRate,
                sellRate,
                bar.Close,
                ema20[entryIndex + offset]);
        }

        return null;
    }

    /// <summary>
    /// The mechanical exit ladder, applied to a simulated training trade: at <c>+1R</c> the stop
    /// ratchets to the fee-paid break-even tick and at <c>+1.5R</c> it trails one causally built
    /// EMA20 behind. Both moves are one-way and neither may cross the target.
    /// </summary>
    private static void RatchetTrainingStop(
        ref decimal stop,
        (decimal Entry, decimal StopLoss, decimal Target) plan,
        decimal breakEvenStop,
        decimal netRiskPerShare,
        decimal buyRate,
        decimal sellRate,
        decimal markClose,
        decimal? ema20)
    {
        if (netRiskPerShare <= 0m || markClose <= 0m)
        {
            return;
        }

        var unrealizedPerShare = (markClose * (1m - sellRate)) - (plan.Entry * (1m + buyRate));
        var unrealizedR = unrealizedPerShare / netRiskPerShare;

        if (unrealizedR >= 1.0m && stop < breakEvenStop && breakEvenStop < plan.Target)
        {
            stop = breakEvenStop;
        }

        if (unrealizedR < 1.5m || ema20 is not decimal ema)
        {
            return;
        }

        var trailedStop = PseBoardLotService.RoundToValidTick(ema);
        if (trailedStop > stop && trailedStop < plan.Target)
        {
            stop = trailedStop;
        }
    }

    /// <summary>
    /// Realized R after the desk's <b>effective</b> round trip, using the same risk denominator
    /// the live order ticket uses so a learned expectancy is quoted in the units the desk
    /// trades. Each leg pays its own rate: the buy leg charges commission and VAT, the sell leg
    /// adds the 0.10% stock transaction tax, and both carry half the spread/impact estimate.
    /// </summary>
    private static decimal NetRealizedR(
        decimal entry,
        decimal exit,
        decimal buyRate,
        decimal sellRate,
        decimal netRiskPerShare)
    {
        if (netRiskPerShare <= 0m || exit <= 0m)
        {
            return 0m;
        }

        var netPerShare = (exit * (1m - sellRate)) - (entry * (1m + buyRate));

        return decimal.Round(netPerShare / netRiskPerShare, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Causal EMA20 per bar: each value is seeded from that ticker's own closes up to and
    /// including that bar, so the trail can never peek forward.
    /// </summary>
    private static decimal?[] BuildEma20Series(IReadOnlyList<OhlcvBar> bars)
    {
        var series = new decimal?[bars.Count];
        var closes = new List<decimal>(bars.Count);

        for (var index = 0; index < bars.Count; index++)
        {
            closes.Add(bars[index].Close);
            series[index] = QuantitativeFilterService.ComputeEma(closes);
        }

        return series;
    }

    /// <summary>
    /// Cheap rolling gates evaluated once per ticker so the expensive full metric pass only
    /// runs on sessions that could plausibly pass the horizon entry filter.
    /// </summary>
    private sealed class PrefixPrefilter
    {
        private readonly IReadOnlyList<OhlcvBar> _bars;
        private readonly decimal?[] _sma50;
        private readonly decimal?[] _sma200;
        private readonly decimal?[] _atr14;
        private readonly decimal[] _volumeAverage20d;

        private PrefixPrefilter(
            IReadOnlyList<OhlcvBar> bars,
            decimal?[] sma50,
            decimal?[] sma200,
            decimal?[] atr14,
            decimal[] volumeAverage20d)
        {
            _bars = bars;
            _sma50 = sma50;
            _sma200 = sma200;
            _atr14 = atr14;
            _volumeAverage20d = volumeAverage20d;
        }

        public static PrefixPrefilter Build(IReadOnlyList<OhlcvBar> bars)
        {
            var volumeAverage = new decimal[bars.Count];

            if (bars.Count >= 20)
            {
                long running = 0;
                for (var index = 0; index < 20; index++)
                {
                    running += bars[index].Volume;
                }

                volumeAverage[19] = running / 20m;

                for (var index = 20; index < bars.Count; index++)
                {
                    running += bars[index].Volume - bars[index - 20].Volume;
                    volumeAverage[index] = running / 20m;
                }
            }

            return new PrefixPrefilter(
                bars,
                SimpleMovingAverage(bars, 50),
                SimpleMovingAverage(bars, 200),
                WilderAtr(bars, 14),
                volumeAverage);
        }

        /// <summary>Mirrors the live gate closely enough to keep the expensive pass rare.</summary>
        public bool Passes(int index)
        {
            if (index <= 0 || index >= _bars.Count)
            {
                return false;
            }

            var close = _bars[index].Close;
            var sma50 = _sma50[index];
            var sma200 = _sma200[index];
            var atr = _atr14[index];
            var averageVolume = _volumeAverage20d[index];

            if (close <= 0m || sma50 is null || sma200 is null || atr is null || averageVolume <= 0m)
            {
                return false;
            }

            return close > sma50.Value &&
                   close > sma200.Value &&
                   _bars[index].Volume >= 1.2m * averageVolume &&
                   atr.Value / close >= 0.01m;
        }

        private static decimal?[] SimpleMovingAverage(IReadOnlyList<OhlcvBar> bars, int period)
        {
            var result = new decimal?[bars.Count];
            if (bars.Count < period)
            {
                return result;
            }

            var running = 0m;
            for (var index = 0; index < period; index++)
            {
                running += bars[index].Close;
            }

            result[period - 1] = running / period;

            for (var index = period; index < bars.Count; index++)
            {
                running += bars[index].Close - bars[index - period].Close;
                result[index] = running / period;
            }

            return result;
        }

        private static decimal?[] WilderAtr(IReadOnlyList<OhlcvBar> bars, int period)
        {
            var result = new decimal?[bars.Count];
            if (bars.Count <= period)
            {
                return result;
            }

            var trueRange = new decimal[bars.Count];

            for (var index = 1; index < bars.Count; index++)
            {
                var previousClose = bars[index - 1].Close;
                var high = Math.Max(bars[index].High, previousClose);
                var low = Math.Min(bars[index].Low, previousClose);
                trueRange[index] = high - low;
            }

            var running = 0m;
            for (var index = 1; index <= period; index++)
            {
                running += trueRange[index];
            }

            running /= period;
            result[period] = running;

            for (var index = period + 1; index < bars.Count; index++)
            {
                running = ((running * (period - 1)) + trueRange[index]) / period;
                result[index] = running;
            }

            return result;
        }
    }

    /// <summary>
    /// Fits one horizon and decides whether it earned the right to price live picks. The split
    /// is chronological: the first 70% of resolved trades fit the weights, the last 30% grade
    /// them. Training trades whose forward window had not cleared before the first held-out
    /// entry are purged, and one extra forward window of embargo is left between the slices.
    /// </summary>
    private TrainedHorizonModel Fit(SignalHorizon horizon, IReadOnlyList<TrainingTrial> trials)
    {
        var profile = HorizonProfile.For(horizon);
        var nowUtc = DateTime.UtcNow;
        var embargo = Math.Max(1, profile.ForwardWindowBars);

        var ordered = trials
            .OrderBy(trade => trade.EntryDate)
            .ThenBy(trade => trade.Ticker, StringComparer.Ordinal)
            .ToList();

        if (ordered.Count < MinimumTrainingTrialsForFit)
        {
            return BuildPriorFallback(horizon, ordered, nowUtc, embargo);
        }

        var splitIndex = Math.Clamp(
            (int)Math.Floor(ordered.Count * (double)TrainFraction),
            1,
            ordered.Count - 1);

        var validationStart = ordered[splitIndex].EntryDate;

        var train = ordered
            .Take(splitIndex)
            .Where(trade => WeekdaySessionsBetween(trade.ExitDate, validationStart) >= embargo)
            .ToList();
        var validation = ordered.Skip(splitIndex).ToList();

        if (train.Count < MinimumTrainingTrialsForFit || validation.Count == 0)
        {
            return BuildPriorFallback(horizon, ordered, nowUtc, embargo);
        }

        var (means, scales) = FitFeatureStats(train);
        var trainX = train.Select(trade => Standardize(trade.Features, means, scales)).ToArray();
        var validationX = validation.Select(trade => Standardize(trade.Features, means, scales)).ToArray();
        var trainY = train.Select(trade => trade.Won ? 1d : 0d).ToArray();
        var validationY = validation.Select(trade => trade.Won ? 1d : 0d).ToArray();

        // Residual learning: the structural prior enters the linear predictor as an offset, so
        // the weights explain the edge the heuristic missed instead of restating it.
        var trainOffsets = train.Select(trade => LogisticModelTrainer.Logit((double)trade.StructuralPrior)).ToArray();
        var validationOffsets = validation.Select(trade => LogisticModelTrainer.Logit((double)trade.StructuralPrior)).ToArray();

        // Date clusters and payoff magnitude: a 25-name index-surge session must not outvote 25
        // idiosyncratic setups, and a +2.5R target hit must carry more weight than a +0.02R scratch.
        var trainWeights = ComputeSampleWeights(train);

        var baseRate = ShrunkBaseRate(train);
        var baselineProbabilities = validationY.Select(_ => baseRate).ToArray();
        var baselineBrier = BrierScore(baselineProbabilities, validationY);
        var baselineLogLoss = LogLoss(baselineProbabilities, validationY);

        FitChoice? best = null;

        foreach (var lambda in LambdaGrid)
        {
            var fitted = LogisticModelTrainer.Fit(
                trainX,
                trainY,
                (double)lambda,
                sampleWeights: trainWeights,
                offsets: trainOffsets);
            var probabilities = validationX
                .Select((row, index) => LogisticModelTrainer.Predict(fitted, row, validationOffsets[index]))
                .ToArray();
            var brier = BrierScore(probabilities, validationY);
            var logLoss = LogLoss(probabilities, validationY);

            var improved = best is null ||
                           brier < best.Brier ||
                           (Math.Abs(brier - best.Brier) <= 1e-9 && logLoss < best.LogLoss);

            if (improved)
            {
                best = new FitChoice(fitted, lambda, brier, logLoss, probabilities);
            }
        }

        best ??= new FitChoice(
            LogisticModelTrainer.Fit(
                trainX,
                trainY,
                (double)LambdaGrid[^1],
                sampleWeights: trainWeights,
                offsets: trainOffsets),
            LambdaGrid[^1],
            baselineBrier,
            baselineLogLoss,
            baselineProbabilities);

        var trainedBrier = best.Brier;

        // The held-out trades the model would actually have *funded*: positive expected value at
        // the model's own probability and the friction the ticket charges. A fit that only
        // improves calibration while funding losing trades is not a money manager.
        var policyExpectancyR = ComputePolicyExpectancyR(validation, best.ValidationProbabilities);

        var isActive = validation.Count >= MinimumValidationTrials &&
                       train.Count + validation.Count >= MinimumTrials &&
                       trainedBrier < baselineBrier &&
                       best.LogLoss <= baselineLogLoss &&
                       policyExpectancyR > 0m;

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var stats = new Dictionary<string, FeatureStat>(StringComparer.Ordinal);

        for (var index = 0; index < TrainedModelFeatures.Length; index++)
        {
            weights[TrainedModelFeatures.Keys[index]] = Math.Round(best.Fitted.Weights[index], 6);

            // A factor that never showed up is stored with a neutral mean and unit scale, so
            // inference imputes zero contribution instead of dividing by an empty range.
            stats[TrainedModelFeatures.Keys[index]] = new FeatureStat(
                Math.Round(means[index], 6),
                Math.Round(scales[index], 6));
        }

        return new TrainedHorizonModel
        {
            Horizon = horizon,
            TrainedAtUtc = nowUtc,
            TrainStartDate = train[0].EntryDate,
            TrainEndDate = train.Max(trade => trade.ExitDate),
            ValidationStartDate = validationStart,
            ValidationEndDate = validation.Max(trade => trade.ExitDate),
            TrainSampleCount = train.Count,
            ValidationSampleCount = validation.Count,
            EmbargoBars = embargo,
            BaselineBrierScore = decimal.Round((decimal)baselineBrier, 6, MidpointRounding.AwayFromZero),
            TrainedBrierScore = decimal.Round((decimal)trainedBrier, 6, MidpointRounding.AwayFromZero),
            BaselineLogLoss = decimal.Round((decimal)baselineLogLoss, 6, MidpointRounding.AwayFromZero),
            TrainedLogLoss = decimal.Round((decimal)best.LogLoss, 6, MidpointRounding.AwayFromZero),
            ValidationWinRate = decimal.Round(WinRate(validation), 4, MidpointRounding.AwayFromZero),
            ValidationExpectancyR = decimal.Round(ExpectancyR(validation), 4, MidpointRounding.AwayFromZero),
            ValidationProfitFactor = decimal.Round(ProfitFactor(validation), 4, MidpointRounding.AwayFromZero),
            OptimalKellyFractionMultiplier = OptimalKelly(validation, best.ValidationProbabilities),
            Intercept = decimal.Round((decimal)best.Fitted.Intercept, 6, MidpointRounding.AwayFromZero),
            WeightsJson = TrainedModelJson.WriteWeights(weights),
            FeatureStatsJson = TrainedModelJson.WriteStats(stats),
            IsActive = isActive
        };
    }

    /// <summary>
    /// Stores an honest "not enough evidence" row: the neutral prior, zero weights and a
    /// failed gate. Live pricing keeps using the structural prior, but the terminal can now
    /// say exactly how much resolved data the trainer actually had.
    /// </summary>
    private static TrainedHorizonModel BuildPriorFallback(
        SignalHorizon horizon,
        IReadOnlyList<TrainingTrial> trials,
        DateTime nowUtc,
        int embargo)
    {
        var labels = trials.Select(trade => trade.Won ? 1d : 0d).ToArray();
        var wins = labels.Sum();
        var baseRate = (wins
                        + (double)(ProbabilisticEdgeService.NeutralPriorWinRate * ProbabilisticEdgeService.PriorSampleSize))
                       / (trials.Count + ProbabilisticEdgeService.PriorSampleSize);

        var probabilities = labels.Select(_ => baseRate).ToArray();
        var brier = trials.Count == 0 ? 0.25d : BrierScore(probabilities, labels);
        var logLoss = trials.Count == 0 ? Math.Log(2d) : LogLoss(probabilities, labels);
        var fallbackDate = trials.Count > 0 ? trials[0].EntryDate : DateOnly.FromDateTime(nowUtc);

        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        var stats = new Dictionary<string, FeatureStat>(StringComparer.Ordinal);

        foreach (var key in TrainedModelFeatures.Keys)
        {
            weights[key] = 0d;
            stats[key] = new FeatureStat(0d, 1d);
        }

        return new TrainedHorizonModel
        {
            Horizon = horizon,
            TrainedAtUtc = nowUtc,
            TrainStartDate = fallbackDate,
            TrainEndDate = trials.Count > 0 ? trials.Max(trade => trade.ExitDate) : fallbackDate,
            ValidationStartDate = trials.Count > 0 ? trials[^1].EntryDate : fallbackDate,
            ValidationEndDate = trials.Count > 0 ? trials[^1].ExitDate : fallbackDate,
            TrainSampleCount = 0,
            ValidationSampleCount = trials.Count,
            EmbargoBars = embargo,
            BaselineBrierScore = decimal.Round((decimal)brier, 6, MidpointRounding.AwayFromZero),
            TrainedBrierScore = decimal.Round((decimal)brier, 6, MidpointRounding.AwayFromZero),
            BaselineLogLoss = decimal.Round((decimal)logLoss, 6, MidpointRounding.AwayFromZero),
            TrainedLogLoss = decimal.Round((decimal)logLoss, 6, MidpointRounding.AwayFromZero),
            ValidationWinRate = decimal.Round(WinRate(trials), 4, MidpointRounding.AwayFromZero),
            ValidationExpectancyR = decimal.Round(ExpectancyR(trials), 4, MidpointRounding.AwayFromZero),
            ValidationProfitFactor = decimal.Round(ProfitFactor(trials), 4, MidpointRounding.AwayFromZero),
            OptimalKellyFractionMultiplier = trials.Count == 0
                ? NeutralKellyFraction
                : OptimalKelly(trials, trials.Select(_ => baseRate).ToArray()),
            Intercept = decimal.Round(
                (decimal)Math.Log(baseRate / (1d - baseRate)),
                6,
                MidpointRounding.AwayFromZero),
            WeightsJson = TrainedModelJson.WriteWeights(weights),
            FeatureStatsJson = TrainedModelJson.WriteStats(stats),
            IsActive = false
        };
    }

    /// <summary>
    /// Mean and sample standard deviation are fitted on the training slice only. Computing
    /// them on the whole history would leak held-out scale into the fit.
    /// </summary>
    private static (double[] Means, double[] Scales) FitFeatureStats(IReadOnlyList<TrainingTrial> train)
    {
        var means = new double[TrainedModelFeatures.Length];
        var scales = new double[TrainedModelFeatures.Length];

        for (var index = 0; index < TrainedModelFeatures.Length; index++)
        {
            var observed = new List<double>(train.Count);

            foreach (var trade in train)
            {
                if (trade.Features[index] is decimal value)
                {
                    observed.Add((double)value);
                }
            }

            if (observed.Count < 2)
            {
                means[index] = observed.Count == 1 ? observed[0] : 0d;
                scales[index] = 1d;
                continue;
            }

            var mean = observed.Average();
            var variance = observed.Sum(value => (value - mean) * (value - mean)) / (observed.Count - 1);

            means[index] = mean;
            scales[index] = variance > 0d ? Math.Sqrt(variance) : 1d;
        }

        return (means, scales);
    }

    /// <summary>
    /// Standardizes one trade. A missing input is mean-imputed on the training distribution,
    /// which lands at zero after scaling: the model abstains instead of guessing a level.
    /// </summary>
    private static double[] Standardize(decimal?[] features, double[] means, double[] scales)
    {
        var row = new double[TrainedModelFeatures.Length];

        for (var index = 0; index < row.Length; index++)
        {
            row[index] = features[index] is decimal value
                ? ((double)value - means[index]) / scales[index]
                : 0d;
        }

        return row;
    }

    private static double ShrunkBaseRate(IReadOnlyList<TrainingTrial> train)
    {
        var wins = train.Count(trade => trade.Won);

        return (wins + (double)(ProbabilisticEdgeService.NeutralPriorWinRate * ProbabilisticEdgeService.PriorSampleSize))
               / (train.Count + ProbabilisticEdgeService.PriorSampleSize);
    }

    private static double BrierScore(double[] probabilities, double[] labels)
    {
        if (probabilities.Length == 0 || probabilities.Length != labels.Length)
        {
            return 0.25d;
        }

        var sum = 0d;

        for (var index = 0; index < probabilities.Length; index++)
        {
            var difference = probabilities[index] - labels[index];
            sum += difference * difference;
        }

        return sum / probabilities.Length;
    }

    private static double LogLoss(double[] probabilities, double[] labels)
    {
        if (probabilities.Length == 0 || probabilities.Length != labels.Length)
        {
            return Math.Log(2d);
        }

        var sum = 0d;

        for (var index = 0; index < probabilities.Length; index++)
        {
            var probability = Math.Clamp(probabilities[index], 1e-6d, 1d - 1e-6d);
            sum += labels[index] > 0.5d
                ? -Math.Log(probability)
                : -Math.Log(1d - probability);
        }

        return sum / probabilities.Length;
    }

    private static decimal WinRate(IReadOnlyList<TrainingTrial> trials) =>
        trials.Count == 0
            ? 0m
            : (decimal)trials.Count(trade => trade.Won) / trials.Count;

    private static decimal ExpectancyR(IReadOnlyList<TrainingTrial> trials) =>
        trials.Count == 0
            ? 0m
            : trials.Sum(trade => trade.RealizedR) / trials.Count;

    private static decimal ProfitFactor(IReadOnlyList<TrainingTrial> trials)
    {
        var grossProfit = trials.Where(trade => trade.RealizedR > 0m).Sum(trade => trade.RealizedR);
        var grossLoss = Math.Abs(trials.Where(trade => trade.RealizedR < 0m).Sum(trade => trade.RealizedR));

        if (grossLoss > 0m)
        {
            return grossProfit / grossLoss;
        }

        return grossProfit > 0m ? 99m : 0m;
    }

    /// <summary>
    /// Scans the Kelly grid on the held-out trades, compounding them in exit order. For every
    /// trial <i>i</i> the model's own predicted probability and the friction-inclusive
    /// <c>(reward/risk)</c> give the full-Kelly fraction
    /// <c>f*_i = max(0, p_i - (1 - p_i) / b_i)</c>; only positive-EV setups are funded, and each
    /// one risks <c>clamp(c * f*_i, 0, MaxHeldOutStakeFraction)</c> of equity. The score rewards
    /// log growth but charges half the peak-to-trough drawdown, so a stake that only wins by
    /// taking a violent path is not selected.
    /// <para>
    /// The multiplier returned is <c>c</c> — the fraction of full Kelly — in the units
    /// <see cref="LearnedEdgeModel.KellyScale"/> rebases against the neutral 0.25x. It is
    /// therefore a sizing preference, not a per-trade risk figure: 0.25 means "stake a quarter
    /// of full Kelly", never "risk a quarter of the account".
    /// </para>
    /// </summary>
    private static decimal OptimalKelly(IReadOnlyList<TrainingTrial> validation, double[] probabilities)
    {
        if (validation.Count == 0 || probabilities.Length != validation.Count)
        {
            return NeutralKellyFraction;
        }

        var ordered = validation
            .Select((trade, index) => (Trade: trade, Probability: probabilities[index]))
            .OrderBy(entry => entry.Trade.ExitDate)
            .ToList();

        var funded = ordered
            .Select(entry =>
            {
                var probability = entry.Probability;
                var rewardRisk = (double)entry.Trade.NetRiskReward;
                var expectedValue = (probability * rewardRisk) - (1d - probability);
                var fullKelly = rewardRisk > 0d
                    ? Math.Max(0d, probability - ((1d - probability) / rewardRisk))
                    : 0d;

                return (entry.Trade, ExpectedValue: expectedValue, FullKelly: fullKelly);
            })
            .Where(entry => entry.ExpectedValue > 0d && entry.FullKelly > 0d)
            .ToList();

        if (funded.Count == 0)
        {
            return NeutralKellyFraction;
        }

        var bestFraction = NeutralKellyFraction;
        var bestScore = double.MinValue;

        foreach (var fraction in KellyGrid)
        {
            var equity = 1d;
            var peak = 1d;
            var maxDrawdown = 0d;

            foreach (var (trade, _, fullKelly) in funded)
            {
                var stake = Math.Clamp(
                    (double)fraction * fullKelly,
                    0d,
                    (double)MaxHeldOutStakeFraction);

                equity *= Math.Max(0.01d, 1d + (stake * (double)trade.RealizedR));
                peak = Math.Max(peak, equity);
                maxDrawdown = Math.Max(maxDrawdown, (peak - equity) / peak);
            }

            var logGrowth = Math.Log(Math.Max(equity, 1e-9d)) / funded.Count;
            var score = logGrowth - (0.5d * maxDrawdown);

            if (score > bestScore)
            {
                bestScore = score;
                bestFraction = fraction;
            }
        }

        return bestFraction;
    }

    /// <summary>
    /// Mean realized R of the held-out trades the model would have funded (predicted
    /// probability high enough to make expected value positive at that trade's own net
    /// reward/risk). Zero when nothing qualifies, which fails the promotion gate.
    /// </summary>
    private static decimal ComputePolicyExpectancyR(
        IReadOnlyList<TrainingTrial> validation,
        double[] probabilities)
    {
        if (validation.Count == 0 || probabilities.Length != validation.Count)
        {
            return 0m;
        }

        var funded = new List<decimal>(validation.Count);

        for (var index = 0; index < validation.Count; index++)
        {
            var rewardRisk = validation[index].NetRiskReward;

            if (rewardRisk <= 0m)
            {
                continue;
            }

            var probability = (decimal)probabilities[index];
            var expectedValue = (probability * rewardRisk) - (1m - probability);

            if (expectedValue > 0m)
            {
                funded.Add(validation[index].RealizedR);
            }
        }

        return funded.Count == 0
            ? 0m
            : decimal.Round(funded.Sum() / funded.Count, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Per-trial fitting weight: <c>1 / sqrt(signals on the same entry date)</c> so one
    /// market-wide beta session cannot dominate the fit, times
    /// <c>clamp(0.5 + 0.5 * |RealizedR|, 0.5, 2.0)</c> so a target hit outranks a scratch. The
    /// vector is normalised to mean 1, which keeps the L2 penalty on a comparable scale.
    /// </summary>
    private static double[] ComputeSampleWeights(IReadOnlyList<TrainingTrial> trials)
    {
        var weights = new double[trials.Count];
        var perDate = trials
            .GroupBy(trade => trade.EntryDate)
            .ToDictionary(group => group.Key, group => group.Count());

        for (var index = 0; index < trials.Count; index++)
        {
            var clusterWeight = 1d / Math.Sqrt(perDate[trials[index].EntryDate]);
            var magnitude = Math.Clamp(
                0.5d + (0.5d * Math.Abs((double)trials[index].RealizedR)),
                (double)MinimumMagnitudeWeight,
                (double)MaximumMagnitudeWeight);

            weights[index] = clusterWeight * magnitude;
        }

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

    /// <summary>
    /// Trading sessions between two dates, weekends excluded. PSE holidays only make the gap
    /// longer in calendar terms, which errs on the safe side of the embargo.
    /// </summary>
    private static int WeekdaySessionsBetween(DateOnly fromExclusive, DateOnly toExclusive)
    {
        var sessions = 0;

        for (var cursor = fromExclusive.AddDays(1); cursor < toExclusive; cursor = cursor.AddDays(1))
        {
            if (cursor.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                sessions++;
            }
        }

        return sessions;
    }

    /// <summary>
    /// Causal PSEi regime: the 50- and 200-session averages are recomputed from the index
    /// prefix ending on each date, so the gate a historical session is judged against uses only
    /// what the desk could have seen that afternoon. The live feed's own
    /// <see cref="MarketDataBatch.MarketRegime"/> is today's reading and would leak the future
    /// into a four-year replay.
    /// </summary>
    private sealed class CausalIndexRegime
    {
        private readonly List<(DateOnly Date, MarketRegime? Regime, bool IsBearish)> _readings;

        private CausalIndexRegime(List<(DateOnly Date, MarketRegime? Regime, bool IsBearish)> readings) =>
            _readings = readings;

        public static CausalIndexRegime Build(IReadOnlyList<OhlcvBar> indexBars)
        {
            var readings = new List<(DateOnly, MarketRegime?, bool)>(indexBars.Count);
            var closes = new List<decimal>(indexBars.Count);

            foreach (var bar in indexBars.OrderBy(entry => entry.Date))
            {
                closes.Add(bar.Close);

                var sma50 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaShortPeriod);
                var sma200 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaLongPeriod);

                if (sma50 is not decimal shortAverage || sma200 is not decimal longAverage)
                {
                    // Not enough index history yet: the regime is unknown and the gate stays open,
                    // which is the same conservative default a live request without a regime uses.
                    readings.Add((bar.Date, null, false));
                    continue;
                }

                var regime = bar.Close >= shortAverage && bar.Close >= longAverage
                    ? MarketRegime.BULLISH
                    : bar.Close < shortAverage && bar.Close < longAverage
                        ? MarketRegime.BEARISH
                        : MarketRegime.NEUTRAL;

                readings.Add((bar.Date, regime, regime == MarketRegime.BEARISH));
            }

            return new CausalIndexRegime(readings);
        }

        /// <summary>
        /// True when the last index close on or before <paramref name="sessionDate"/> was under
        /// both its 50- and 200-session average. Unknown history is never treated as bearish.
        /// </summary>
        public bool IsBearishAt(DateOnly sessionDate) =>
            TryRead(sessionDate)?.IsBearish == true;

        /// <summary>The causal regime in force at that session, or null when the index is unknown.</summary>
        public MarketRegime? RegimeAt(DateOnly sessionDate) => TryRead(sessionDate)?.Regime;

        private (DateOnly Date, MarketRegime? Regime, bool IsBearish)? TryRead(DateOnly sessionDate)
        {
            (DateOnly Date, MarketRegime? Regime, bool IsBearish)? found = null;

            foreach (var reading in _readings)
            {
                if (reading.Date > sessionDate)
                {
                    break;
                }

                found = reading;
            }

            return found;
        }
    }

    /// <summary>One resolved historical trade with the factor snapshot taken at its entry.</summary>
    private sealed record TrainingTrial(
        string Ticker,
        DateOnly EntryDate,
        DateOnly ExitDate,
        bool Won,
        decimal RealizedR,
        decimal NetRiskReward,
        decimal StructuralPrior,
        decimal?[] Features);

    /// <summary>
    /// The graded outcome of one simulated session: the tick-grid plan it was priced on, the
    /// friction-inclusive reward/risk that plan offered, and the effective round-trip rate that
    /// produced <see cref="RealizedR"/>.
    /// </summary>
    private readonly record struct TrainingOutcome(
        DateOnly ExitDate,
        bool Won,
        decimal RealizedR,
        decimal PlanEntry,
        decimal StopLoss,
        decimal Target,
        decimal NetRiskReward,
        decimal FeeRate);

    private sealed record FitChoice(
        LogisticModelTrainer.FitResult Fitted,
        decimal Lambda,
        double Brier,
        double LogLoss,
        double[] ValidationProbabilities);

    private void LogFit(TrainedHorizonModel model) =>
        _logger?.LogInformation(
            "Trained the {Horizon} money-manager model: {Train} train / {Validation} validation trades, " +
            "Brier {TrainedBrier} vs {BaselineBrier} baseline, active {Active}.",
            model.Horizon,
            model.TrainSampleCount,
            model.ValidationSampleCount,
            model.TrainedBrierScore,
            model.BaselineBrierScore,
            model.IsActive);
}
