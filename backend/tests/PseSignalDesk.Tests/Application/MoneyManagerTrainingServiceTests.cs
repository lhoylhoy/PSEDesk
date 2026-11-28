using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The trainer is the only component allowed to invent weights, so its contract is strict:
/// no live history is a hard failure, every horizon keeps exactly one row, the stored JSON
/// always carries all twelve factors, and a model that has not earned its promotion is stored
/// inactive instead of quietly pricing live picks.
/// </summary>
public sealed class MoneyManagerTrainingServiceTests
{
    private const int BarCount = 240;

    [Fact]
    public async Task TrainAsync_refuses_to_fit_weights_when_the_feed_has_no_history()
    {
        var repository = new RecordingSignalRepository();
        var service = CreateService(Array.Empty<OhlcvBar>(), repository);

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => service.TrainAsync(SignalHorizon.WEEK, LastDate(BarCount)));

        Assert.Contains("cannot be trained", error.Message, StringComparison.Ordinal);
        Assert.Empty(repository.SavedModels);
    }

    [Fact]
    public async Task TrainAllAsync_stores_one_inactive_row_per_horizon_with_all_twelve_factors()
    {
        var repository = new RecordingSignalRepository();
        var service = CreateService(BuildOscillatingBars(BarCount), repository);

        var summaries = await service.TrainAllAsync(LastDate(BarCount));

        Assert.Equal(4, summaries.Count);
        Assert.Equal(4, repository.SavedModels.Count);
        Assert.Equal(
            ProbabilisticEdgeService.AllHorizons.Select(horizon => horizon.ToString()).OrderBy(name => name),
            repository.SavedModels.Select(model => model.Horizon.ToString()).OrderBy(name => name));

        foreach (var model in repository.SavedModels)
        {
            var weights = TrainedModelJson.ReadWeights(model.WeightsJson);
            var stats = TrainedModelJson.ReadStats(model.FeatureStatsJson);

            Assert.Equal(TrainedModelFeatures.Keys.Length, weights.Count);
            Assert.Equal(TrainedModelFeatures.Keys.Length, stats.Count);
            Assert.Equal(HorizonProfile.For(model.Horizon).ForwardWindowBars, model.EmbargoBars);
            Assert.True(model.ValidationStartDate >= model.TrainStartDate);
            Assert.True(model.ValidationEndDate >= model.ValidationStartDate);
        }

        foreach (var summary in summaries)
        {
            // This tape offers only a handful of qualifying sessions, so the trainer must
            // publish an honest fallback rather than a promoted model.
            Assert.Equal(TrainedModelStatus.PriorFallback, summary.Status);
            Assert.False(summary.IsActive);
            Assert.Equal(TrainedModelFeatures.Keys.Length, summary.Weights.Count);
        }
    }

    [Fact]
    public async Task TrainAsync_only_touches_the_requested_horizon()
    {
        var repository = new RecordingSignalRepository();
        var service = CreateService(BuildOscillatingBars(BarCount), repository);

        var summary = await service.TrainAsync(SignalHorizon.MONTH, LastDate(BarCount));

        Assert.Equal("MONTH", summary.Horizon);
        Assert.Single(repository.SavedModels);
        Assert.Equal(SignalHorizon.MONTH, repository.SavedModels[0].Horizon);
    }

    [Fact]
    public void SimulateBarrier_charges_pre_cmepa_tax_and_fills_gap_down_stops_at_open()
    {
        var profile = HorizonProfile.For(SignalHorizon.WEEK);
        var metric = new StockDailyMetric
        {
            Ticker = "ICT",
            ClosePrice = 100m,
            Atr14 = 2m,
            AtrPct = 2m,
            AvgTurnover20dPhp = 50_000_000m,
            MedianTurnover20dPhp = 50_000_000m,
            ActiveTradingDays20d = 20
        };

        var preCmepaBars = new List<OhlcvBar>
        {
            new(new DateOnly(2024, 6, 3), 100m, 101m, 99m, 100m, 1_000_000),
            new(new DateOnly(2024, 6, 4), 94m, 95m, 93m, 94m, 1_000_000),
            new(new DateOnly(2024, 6, 5), 95m, 96m, 94m, 95m, 1_000_000)
        };
        var postCmepaBars = new List<OhlcvBar>
        {
            new(new DateOnly(2025, 8, 4), 100m, 101m, 99m, 100m, 1_000_000),
            new(new DateOnly(2025, 8, 5), 94m, 95m, 93m, 94m, 1_000_000),
            new(new DateOnly(2025, 8, 6), 95m, 96m, 94m, 95m, 1_000_000)
        };
        var ema20 = new decimal?[] { 99m, 98m, 98m };

        var preOutcome = MoneyManagerTrainingService.SimulateBarrier(preCmepaBars, 0, ema20, metric, profile);
        var postOutcome = MoneyManagerTrainingService.SimulateBarrier(postCmepaBars, 0, ema20, metric, profile);

        Assert.NotNull(preOutcome);
        Assert.NotNull(postOutcome);
        // Stop was 97.00, session gapped open to 94.00 -> realized loss must be materially worse than -1.0R.
        Assert.True(preOutcome!.Value.RealizedR < -1.5m);
        Assert.True(postOutcome!.Value.RealizedR < -1.5m);
        // Pre-CMEPA charged 0.60% STT vs 0.10% post-CMEPA, so its effective fee rate is 50 bps higher.
        Assert.Equal(0.0050m, preOutcome.Value.FeeRate - postOutcome.Value.FeeRate);
    }

    [Fact]
    public void IsEligible_enforces_median_turnover_and_active_trading_days()
    {
        var profile = HorizonProfile.For(SignalHorizon.WEEK);
        var spikedOnlyMetric = new StockDailyMetric
        {
            Ticker = "THIN",
            ClosePrice = 10m,
            Sma50 = 9m,
            Sma200 = 8m,
            Rsi14 = 55m,
            VolumeRatioVs20d = 1.5m,
            AtrPct = 2.0m,
            WeinsteinStage = "STAGE_2",
            AvgTurnover20dPhp = 10_000_000m,
            MedianTurnover20dPhp = 1_500_000m,
            ActiveTradingDays20d = 12
        };

        Assert.False(MoneyManagerTrainingService.IsEligible(spikedOnlyMetric, profile));
    }

    [Fact]
    public void Fit_rejects_model_whose_held_out_brier_exceeds_coin_flip_or_prior()
    {
        var trials = new List<MoneyManagerTrainingService.TrainingTrial>(120);
        var start = new DateOnly(2024, 1, 2);

        for (var i = 0; i < 120; i++)
        {
            var entry = start.AddDays(i * 2);
            var exit = entry.AddDays(1);
            // Make training labels 80% winners so constant base rate is ~0.80, while validation flips to 50/50
            // (so constant baseRate has terrible Brier ~0.34 while prior at 0.50 has Brier 0.25).
            var inValidation = i >= 84;
            var won = inValidation ? (i % 2 == 0) : (i % 5 != 0);
            var features = new decimal?[TrainedModelFeatures.Length];
            for (var f = 0; f < features.Length; f++)
            {
                features[f] = won ? 1m : -1m;
            }
            if (inValidation)
            {
                // Anti-correlated on validation so trained Brier >= 0.25 even if it beats stale constant baseRate.
                for (var f = 0; f < features.Length; f++)
                {
                    features[f] = won ? -1m : 1m;
                }
            }

            trials.Add(new MoneyManagerTrainingService.TrainingTrial(
                $"T{i:000}",
                entry,
                exit,
                won,
                won ? 1.8m : -1.0m,
                NetRiskReward: 2.0m,
                StructuralPrior: 0.50m,
                features));
        }

        var model = MoneyManagerTrainingService.Fit(SignalHorizon.DAY, trials);

        Assert.False(model.IsActive);
    }

    [Fact]
    public void TrainedModelPresenter_explains_expectancy_rejection_when_brier_beat_baseline()
    {
        var model = new TrainedHorizonModel
        {
            Horizon = SignalHorizon.DAY,
            TrainedAtUtc = DateTime.UtcNow,
            TrainStartDate = new DateOnly(2024, 1, 1),
            TrainEndDate = new DateOnly(2025, 1, 1),
            ValidationStartDate = new DateOnly(2025, 2, 1),
            ValidationEndDate = new DateOnly(2026, 1, 1),
            TrainSampleCount = 100,
            ValidationSampleCount = 40,
            EmbargoBars = 3,
            BaselineBrierScore = 0.231m,
            TrainedBrierScore = 0.229m,
            BaselineLogLoss = 0.656m,
            TrainedLogLoss = 0.651m,
            ValidationWinRate = 0.35m,
            ValidationExpectancyR = -0.19m,
            ValidationProfitFactor = 0.52m,
            OptimalKellyFractionMultiplier = 0.15m,
            IsActive = false
        };

        var dto = TrainedModelPresenter.ToDto(model, "DAY");

        Assert.Equal(TrainedModelStatus.PriorFallback, dto.Status);
        Assert.Contains("positive expectancy gate", dto.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("did not beat the", dto.Summary, StringComparison.Ordinal);
    }

    private static MoneyManagerTrainingService CreateService(
        IReadOnlyList<OhlcvBar> bars,
        ISignalRepository repository)
    {
        var batch = new MarketDataBatch(
            bars.Count > 0 ? bars[^1].Date : LastDate(BarCount),
            PseiClose: 8_500m,
            PseiSma50: 8_400m,
            PseiSma200: 8_100m,
            NetForeignFlowPhp: 1_200_000_000m,
            UsdPhp: 58.3m,
            MarketRegime: MarketRegime.BULLISH,
            RegimeNote: "Risk-on tape.",
            Stocks: bars.Count == 0
                ? Array.Empty<StockMarketData>()
                : new[]
                {
                    new StockMarketData(
                        "ICT",
                        CompanyName: "International Container Terminal Services, Inc.",
                        Sector: "Transportation",
                        Bars: bars,
                        NetForeignTodayPhp: null,
                        Disclosures: Array.Empty<StockDisclosure>())
                },
            Source: "unit-test");

        return new MoneyManagerTrainingService(
            new StubMarketDataProvider(batch),
            new QuantitativeFilterService(),
            repository);
    }

    private static DateOnly LastDate(int barCount) => new DateOnly(2025, 1, 6).AddDays(barCount - 1);

    /// <summary>
    /// The same choppy-but-rising tape the causal replay tests use: the averages slope up and
    /// volume only expands around session 210, so the entry gate fires on a handful of dates
    /// while the liquid-universe floor is comfortably cleared.
    /// </summary>
    private static List<OhlcvBar> BuildOscillatingBars(int barCount)
    {
        var bars = new List<OhlcvBar>(barCount);
        var date = new DateOnly(2025, 1, 6);
        var price = 50m;

        for (var index = 0; index < barCount; index++)
        {
            var close = price + ((index % 3) switch { 0 => 0.8m, 1 => 0.4m, _ => -0.6m });
            var volume = index is >= 210 and <= 215 ? 3_000_000L : 600_000L;

            bars.Add(new OhlcvBar(
                date.AddDays(index),
                Open: price,
                High: Math.Max(price, close) + 2.0m,
                Low: Math.Min(price, close) - 2.0m,
                Close: close,
                Volume: volume));

            price = close;
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

    private sealed class RecordingSignalRepository : ISignalRepository
    {
        public List<TrainedHorizonModel> SavedModels { get; } = new();

        public Task<DailyMarketSnapshot?> GetSnapshotAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DailyMarketSnapshot?>(null);

        public Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<DailyMarketSnapshot?>(null);

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StockDailyMetric?> GetMetricAsync(
            DateOnly tradeDate,
            string ticker,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DateOnly>> GetRunDatesAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignalRun?> GetLatestRunAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignalRun?> GetRunAsync(
            DateOnly runDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignalRun?> GetPreviousRunAsync(
            DateOnly beforeDate,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SignalRun>> GetRunsForTickerAsync(
            string ticker,
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

        public Task<TrainedHorizonModel?> GetTrainedModelAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<TrainedHorizonModel?>(
                SavedModels.FirstOrDefault(model => model.Horizon == horizon));

        public Task SaveTrainedModelAsync(
            TrainedHorizonModel model,
            CancellationToken cancellationToken = default)
        {
            SavedModels.RemoveAll(existing => existing.Horizon == model.Horizon);
            SavedModels.Add(model);

            return Task.CompletedTask;
        }
    }
}
