using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// Persistence boundary for market snapshots, computed metrics and signal runs.
/// </summary>
public interface ISignalRepository
{
    Task<DailyMarketSnapshot?> GetSnapshotAsync(DateOnly tradeDate, CancellationToken cancellationToken = default);

    Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockDailyMetric>> GetMetricsAsync(DateOnly tradeDate, CancellationToken cancellationToken = default);

    Task<StockDailyMetric?> GetMetricAsync(DateOnly tradeDate, string ticker, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DateOnly>> GetRunDatesAsync(SignalHorizon horizon, CancellationToken cancellationToken = default);

    Task<SignalRun?> GetLatestRunAsync(SignalHorizon horizon, CancellationToken cancellationToken = default);

    Task<SignalRun?> GetRunAsync(DateOnly runDate, SignalHorizon horizon, CancellationToken cancellationToken = default);

    Task<SignalRun?> GetPreviousRunAsync(
        DateOnly beforeDate,
        SignalHorizon horizon,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SignalRun>> GetRunsForTickerAsync(
        string ticker,
        SignalHorizon horizon,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Every stored run (items included) for the outcome ledger and scorecard. A null
    /// horizon returns all four books.
    /// </summary>
    Task<IReadOnlyList<SignalRun>> GetAllRunsAsync(
        SignalHorizon? horizon = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stored metric rows on or after <paramref name="fromDate"/>, used to grade prior
    /// picks against the sessions that followed them without refetching a feed.
    /// </summary>
    Task<IReadOnlyList<StockDailyMetric>> GetMetricsRangeAsync(
        DateOnly fromDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists resolved outcome fields on already-stored signal items.
    /// </summary>
    Task UpdateResolvedOutcomesAsync(
        IReadOnlyList<SignalItem> items,
        CancellationToken cancellationToken = default);

    Task SaveMarketDataAsync(
        MarketDataBatch batch,
        IReadOnlyList<StockDailyMetric> metrics,
        CancellationToken cancellationToken = default);

    Task SaveSignalRunAsync(SignalRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// The active learned model for one horizon, or null when none has been fitted yet.
    /// Defaulted so lightweight test doubles written before the money-manager trainer still
    /// compile; the real repository overrides it.
    /// </summary>
    Task<TrainedHorizonModel?> GetTrainedModelAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<TrainedHorizonModel?>(null);

    /// <summary>Every stored horizon model, newest row per horizon.</summary>
    Task<IReadOnlyList<TrainedHorizonModel>> GetTrainedModelsAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TrainedHorizonModel>>(Array.Empty<TrainedHorizonModel>());

    /// <summary>
    /// Upserts one horizon's model. A horizon has exactly one row: retraining replaces the
    /// previous weights instead of accumulating contradictory ones.
    /// </summary>
    Task SaveTrainedModelAsync(
        TrainedHorizonModel model,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
