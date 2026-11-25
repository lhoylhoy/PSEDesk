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
