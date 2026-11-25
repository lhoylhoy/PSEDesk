using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// The desk's self-training layer. It replays the real PSE tape, labels every qualifying
/// session with the horizon's own ATR-barrier outcome (stop first on ambiguity, fees paid),
/// fits the twelve scale-free factor weights on the first 70% of the labelled trades, purges
/// and embargoes the overlap with the forward windows, and then grades the fit on the last
/// 30% it never saw. A model is promoted to live pricing only when the held-out sample is
/// large enough and its Brier score beats the neutral baseline; otherwise the row is stored
/// inactive so the terminal can explain the fallback instead of hiding it.
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

    private readonly IMarketDataProvider _marketData;
    private readonly QuantitativeFilterService _quantFilter;
    private readonly ISignalRepository _repository;
    private readonly ILogger<MoneyManagerTrainingService>? _logger;

    public MoneyManagerTrainingService(
        IMarketDataProvider marketData,
        QuantitativeFilterService quantFilter,
        ISignalRepository repository,
        ILogger<MoneyManagerTrainingService>? logger = null)
    {
        _marketData = marketData;
        _quantFilter = quantFilter;
        _repository = repository;
        _logger = logger;
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
    /// horizon's own barrier outcome. One metric pass feeds all four horizons: only the
    /// eligibility gates, the barrier multiples and the forward window differ per book.
    /// </summary>
    private Dictionary<SignalHorizon, List<TrainingTrial>> BuildTrials(MarketDataBatch batch)
    {
        var result = ProbabilisticEdgeService.AllHorizons
            .ToDictionary(horizon => horizon, _ => new List<TrainingTrial>());
        var indexBars = batch.IndexBars ?? Array.Empty<OhlcvBar>();
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

            // Only names that are liquid *today* enter the training universe: learning the
            // desk's weights from illiquid names would optimise a book it never trades.
            var latest = _quantFilter.EvaluateStock(
                stock with { Bars = bars },
                batch.TradeDate,
                indexBars,
                computeEmpiricalBaseRate: false);

            if (latest is null || latest.AvgTurnover20dPhp < MinimumTurnoverPhp)
            {
                continue;
            }

            var prefilter = PrefixPrefilter.Build(bars);
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

                foreach (var horizon in ProbabilisticEdgeService.AllHorizons)
                {
                    var profile = HorizonProfile.For(horizon);

                    if (index + profile.ForwardWindowBars >= bars.Count || !IsEligible(metric, profile))
                    {
                        continue;
                    }

                    var outcome = SimulateBarrier(bars, index, metric.ClosePrice, metric.Atr14, profile);
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
                        features));
                }
            }
        }

        return result;
    }

    /// <summary>The horizon's live entry gate, expressed only on the metric of that session.</summary>
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
    /// Labels one session with the horizon's own ATR-barrier outcome. The stop is checked
    /// before the target on every bar (daily OHLC cannot order a same-session touch), and a
    /// window that expires unresolved is graded on the fee-paid close, exactly the way the
    /// forward ledger grades a live pick.
    /// </summary>
    private static TrainingOutcome? SimulateBarrier(
        IReadOnlyList<OhlcvBar> bars,
        int entryIndex,
        decimal entry,
        decimal atr,
        HorizonProfile profile)
    {
        if (entry <= 0m || atr <= 0m)
        {
            return null;
        }

        var stop = entry - (profile.AtrStopMultiplier * atr);
        if (stop <= 0m)
        {
            return null;
        }

        var target = entry + (profile.AtrTargetMultiplier * atr);

        for (var offset = 1; offset <= profile.ForwardWindowBars; offset++)
        {
            var bar = bars[entryIndex + offset];

            if (bar.Low <= stop)
            {
                return new TrainingOutcome(bar.Date, false, NetRealizedR(entry, stop, stop));
            }

            if (bar.High >= target)
            {
                return new TrainingOutcome(bar.Date, true, NetRealizedR(entry, target, stop));
            }
        }

        var expiry = bars[entryIndex + profile.ForwardWindowBars];
        var expiryR = NetRealizedR(entry, expiry.Close, stop);

        return new TrainingOutcome(expiry.Date, expiryR > 0m, expiryR);
    }

    /// <summary>
    /// Realized R after the desk's fees, using the same risk denominator the live order ticket
    /// uses so a learned expectancy is quoted in the units the desk trades. Each leg pays its own
    /// rate: commission and VAT on entry, plus the 0.10% stock transaction tax on the sell leg.
    /// </summary>
    private static decimal NetRealizedR(decimal entry, decimal exit, decimal stop)
    {
        var netRisk = PseBoardLotService.ComputeNetRiskPerShare(entry, stop);
        if (netRisk <= 0m || exit <= 0m)
        {
            return 0m;
        }

        var netPerShare = (exit - entry) -
                          (entry * PseBoardLotService.BuyLegFeeRate) -
                          (exit * PseBoardLotService.SellLegFeeRate);

        return decimal.Round(netPerShare / netRisk, 4, MidpointRounding.AwayFromZero);
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

        var baseRate = ShrunkBaseRate(train);
        var baselineProbabilities = validationY.Select(_ => baseRate).ToArray();
        var baselineBrier = BrierScore(baselineProbabilities, validationY);
        var baselineLogLoss = LogLoss(baselineProbabilities, validationY);

        FitChoice? best = null;

        foreach (var lambda in LambdaGrid)
        {
            var fitted = LogisticModelTrainer.Fit(trainX, trainY, (double)lambda);
            var probabilities = validationX.Select(row => LogisticModelTrainer.Predict(fitted, row)).ToArray();
            var brier = BrierScore(probabilities, validationY);
            var logLoss = LogLoss(probabilities, validationY);

            var improved = best is null ||
                           brier < best.Brier ||
                           (Math.Abs(brier - best.Brier) <= 1e-9 && logLoss < best.LogLoss);

            if (improved)
            {
                best = new FitChoice(fitted, lambda, brier, logLoss);
            }
        }

        best ??= new FitChoice(
            LogisticModelTrainer.Fit(trainX, trainY, (double)LambdaGrid[^1]),
            LambdaGrid[^1],
            baselineBrier,
            baselineLogLoss);

        var trainedBrier = best.Brier;
        var isActive = validation.Count >= MinimumValidationTrials &&
                       train.Count + validation.Count >= MinimumTrials &&
                       trainedBrier < baselineBrier;

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
            OptimalKellyFractionMultiplier = OptimalKelly(validation),
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
                : OptimalKelly(trials),
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
    /// Scans the Kelly grid on the held-out trades, compounding them in exit order. The score
    /// rewards log growth but charges half the peak-to-trough drawdown, so a stake that only
    /// wins by taking a violent path is not selected.
    /// </summary>
    private static decimal OptimalKelly(IReadOnlyList<TrainingTrial> validation)
    {
        if (validation.Count == 0)
        {
            return NeutralKellyFraction;
        }

        var ordered = validation.OrderBy(trade => trade.ExitDate).ToList();
        var bestFraction = NeutralKellyFraction;
        var bestScore = double.MinValue;

        foreach (var fraction in KellyGrid)
        {
            var equity = 1d;
            var peak = 1d;
            var maxDrawdown = 0d;

            foreach (var trade in ordered)
            {
                equity *= Math.Max(0.01d, 1d + ((double)fraction * (double)trade.RealizedR));
                peak = Math.Max(peak, equity);
                maxDrawdown = Math.Max(maxDrawdown, (peak - equity) / peak);
            }

            var logGrowth = Math.Log(Math.Max(equity, 1e-9d)) / ordered.Count;
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

    /// <summary>One resolved historical trade with the factor snapshot taken at its entry.</summary>
    private sealed record TrainingTrial(
        string Ticker,
        DateOnly EntryDate,
        DateOnly ExitDate,
        bool Won,
        decimal RealizedR,
        decimal?[] Features);

    private readonly record struct TrainingOutcome(DateOnly ExitDate, bool Won, decimal RealizedR);

    private sealed record FitChoice(
        LogisticModelTrainer.FitResult Fitted,
        decimal Lambda,
        double Brier,
        double LogLoss);

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
