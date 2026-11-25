using Microsoft.EntityFrameworkCore;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of the signal repository. Reads are no-tracking; writes
/// use short transactions so a run's items and drops persist atomically.
/// </summary>
public sealed class SignalRepository : ISignalRepository
{
    private readonly PseSignalDbContext _db;

    public SignalRepository(PseSignalDbContext db)
    {
        _db = db;
    }

    public Task<DailyMarketSnapshot?> GetSnapshotAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken = default) =>
        _db.DailyMarketSnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TradeDate == tradeDate, cancellationToken);

    public Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
        _db.DailyMarketSnapshots
            .AsNoTracking()
            .OrderByDescending(s => s.TradeDate)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<StockDailyMetric>> GetMetricsAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken = default) =>
        await _db.StockDailyMetrics
            .AsNoTracking()
            .Where(m => m.TradeDate == tradeDate)
            .OrderByDescending(m => m.QuantPreScore)
            .ThenByDescending(m => m.AvgTurnover20dPhp)
            .ThenBy(m => m.Ticker)
            .ToListAsync(cancellationToken);

    public Task<StockDailyMetric?> GetMetricAsync(
        DateOnly tradeDate,
        string ticker,
        CancellationToken cancellationToken = default) =>
        _db.StockDailyMetrics
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.TradeDate == tradeDate && m.Ticker == ticker, cancellationToken);

    public async Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(
        CancellationToken cancellationToken = default) =>
        await _db.DailyMarketSnapshots
            .AsNoTracking()
            .Select(s => s.TradeDate)
            .OrderBy(d => d)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DateOnly>> GetRunDatesAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default) =>
        await _db.SignalRuns
            .AsNoTracking()
            .Where(r => r.Horizon == horizon)
            .Select(r => r.RunDate)
            .Distinct()
            .OrderBy(d => d)
            .ToListAsync(cancellationToken);

    public Task<SignalRun?> GetLatestRunAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default) =>
        _db.SignalRuns
            .AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.Drops)
            .Where(r => r.Horizon == horizon)
            .OrderByDescending(r => r.RunDate)
            .ThenByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<SignalRun?> GetRunAsync(
        DateOnly runDate,
        SignalHorizon horizon,
        CancellationToken cancellationToken = default) =>
        _db.SignalRuns
            .AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.Drops)
            .Where(r => r.RunDate == runDate && r.Horizon == horizon)
            .OrderByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<SignalRun?> GetPreviousRunAsync(
        DateOnly beforeDate,
        SignalHorizon horizon,
        CancellationToken cancellationToken = default) =>
        _db.SignalRuns
            .AsNoTracking()
            .Include(r => r.Items)
            .Include(r => r.Drops)
            .Where(r => r.Horizon == horizon && r.RunDate < beforeDate)
            .OrderByDescending(r => r.RunDate)
            .ThenByDescending(r => r.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<SignalRun>> GetRunsForTickerAsync(
        string ticker,
        SignalHorizon horizon,
        CancellationToken cancellationToken = default) =>
        await _db.SignalRuns
            .AsNoTracking()
            .Include(r => r.Items)
            .Where(r => r.Horizon == horizon && r.Items.Any(i => i.Ticker == ticker))
            .OrderBy(r => r.RunDate)
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<SignalRun>> GetAllRunsAsync(
        SignalHorizon? horizon = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.SignalRuns
            .AsNoTracking()
            .Include(r => r.Items)
            .AsQueryable();

        if (horizon is SignalHorizon requested)
        {
            query = query.Where(r => r.Horizon == requested);
        }

        return await query
            .OrderBy(r => r.RunDate)
            .ThenBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StockDailyMetric>> GetMetricsRangeAsync(
        DateOnly fromDate,
        CancellationToken cancellationToken = default) =>
        await _db.StockDailyMetrics
            .AsNoTracking()
            .Where(m => m.TradeDate >= fromDate)
            .OrderBy(m => m.TradeDate)
            .ThenBy(m => m.Ticker)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Copies the graded outcome fields onto the stored rows. Only the outcome ledger is
    /// touched, so re-grading can never rewrite a thesis or a price level.
    /// </summary>
    public async Task UpdateResolvedOutcomesAsync(
        IReadOnlyList<SignalItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        var ids = items.Select(item => item.Id).Distinct().ToList();
        var stored = await _db.SignalItems
            .Where(item => ids.Contains(item.Id))
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        foreach (var item in items)
        {
            if (!stored.TryGetValue(item.Id, out var entity))
            {
                continue;
            }

            entity.OutcomeStatus = item.OutcomeStatus;
            entity.OutcomeDate = item.OutcomeDate;
            entity.OutcomeExitPrice = item.OutcomeExitPrice;
            entity.RealizedNetR = item.RealizedNetR;
            entity.BarsHeld = item.BarsHeld;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    public async Task SaveMarketDataAsync(
        MarketDataBatch batch,
        IReadOnlyList<StockDailyMetric> metrics,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        var snapshot = await _db.DailyMarketSnapshots
            .FirstOrDefaultAsync(s => s.TradeDate == batch.TradeDate, cancellationToken);

        if (snapshot is null)
        {
            _db.DailyMarketSnapshots.Add(MarketDataMapper.ToSnapshot(batch));
        }
        else
        {
            snapshot.PseiClose = batch.PseiClose;
            snapshot.PseiSma50 = batch.PseiSma50;
            snapshot.PseiSma200 = batch.PseiSma200;
            snapshot.NetForeignFlowPhp = batch.NetForeignFlowPhp;
            snapshot.UsdPhp = batch.UsdPhp;
            snapshot.MarketRegime = batch.MarketRegime;
            snapshot.RegimeNote = batch.RegimeNote;
            snapshot.BreadthAboveSma50Pct = batch.Breadth?.AboveSma50Pct ?? snapshot.BreadthAboveSma50Pct;
            snapshot.BreadthAboveSma200Pct = batch.Breadth?.AboveSma200Pct ?? snapshot.BreadthAboveSma200Pct;
            snapshot.Stage2Count = batch.Breadth?.Stage2Count ?? snapshot.Stage2Count;
            snapshot.EvaluatedStockCount = batch.Breadth?.EvaluatedStockCount ?? snapshot.EvaluatedStockCount;

            // A re-save (for example the daily quotation-report import) must not wipe dials it
            // does not own, so every optional field only overwrites when it actually has a value.
            snapshot.UsdPhpSma50 = batch.UsdPhpSma50 ?? snapshot.UsdPhpSma50;
            snapshot.UsdPhpChange20dPct = batch.UsdPhpChange20dPct ?? snapshot.UsdPhpChange20dPct;
            snapshot.PseiRealizedVol20dPct = batch.PseiRealizedVol20dPct ?? snapshot.PseiRealizedVol20dPct;
            snapshot.SectorRotationJson = batch.SectorRotation is { Count: > 0 }
                ? MarketDataMapper.SerializeSectorRotation(batch.SectorRotation)
                : snapshot.SectorRotationJson;
            snapshot.Us10YClosePct = batch.Us10YClosePct ?? snapshot.Us10YClosePct;
            snapshot.Us10YChange20dBps = batch.Us10YChange20dBps ?? snapshot.Us10YChange20dBps;
            snapshot.BrentUsd = batch.BrentUsd ?? snapshot.BrentUsd;
            snapshot.BrentChange20dPct = batch.BrentChange20dPct ?? snapshot.BrentChange20dPct;
            snapshot.EpheCloseUsd = batch.EpheCloseUsd ?? snapshot.EpheCloseUsd;
            snapshot.EpheChange20dPct = batch.EpheChange20dPct ?? snapshot.EpheChange20dPct;
            snapshot.ForeignFlowSource = batch.ForeignFlowSource ?? snapshot.ForeignFlowSource;
        }

        var existingMetrics = await _db.StockDailyMetrics
            .Where(m => m.TradeDate == batch.TradeDate)
            .ToListAsync(cancellationToken);

        if (existingMetrics.Count > 0)
        {
            _db.StockDailyMetrics.RemoveRange(existingMetrics);
        }

        foreach (var metric in metrics)
        {
            metric.Id = 0;
        }

        _db.StockDailyMetrics.AddRange(metrics);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    public async Task SaveSignalRunAsync(SignalRun run, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        // One run per (date, horizon): a manual re-run replaces the previous list so
        // drift history never double-counts a trading day.
        var existingRuns = await _db.SignalRuns
            .Where(r => r.RunDate == run.RunDate && r.Horizon == run.Horizon)
            .ToListAsync(cancellationToken);

        if (existingRuns.Count > 0)
        {
            _db.SignalRuns.RemoveRange(existingRuns);
        }

        run.Id = 0;
        foreach (var item in run.Items)
        {
            item.Id = 0;
            item.SignalRunId = 0;
        }

        foreach (var drop in run.Drops)
        {
            drop.Id = 0;
            drop.SignalRunId = 0;
        }

        _db.SignalRuns.Add(run);

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }

    public Task<TrainedHorizonModel?> GetTrainedModelAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default) =>
        _db.TrainedHorizonModels
            .AsNoTracking()
            .OrderByDescending(model => model.TrainedAtUtc)
            .FirstOrDefaultAsync(model => model.Horizon == horizon, cancellationToken);

    public async Task<IReadOnlyList<TrainedHorizonModel>> GetTrainedModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var models = await _db.TrainedHorizonModels
            .AsNoTracking()
            .OrderByDescending(model => model.TrainedAtUtc)
            .ToListAsync(cancellationToken);

        // One row per horizon is the storage invariant, but a manual database edit could
        // break it; the newest row wins so a stale duplicate can never price a pick.
        return models
            .GroupBy(model => model.Horizon)
            .Select(group => group.First())
            .OrderBy(model => model.Horizon)
            .ToList();
    }

    /// <summary>
    /// Upserts the horizon's model in place. A retrain replaces the weights, the stats and
    /// the validation telemetry together, so the stored row can never mix two fits.
    /// </summary>
    public async Task SaveTrainedModelAsync(
        TrainedHorizonModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        var stored = await _db.TrainedHorizonModels
            .FirstOrDefaultAsync(existing => existing.Horizon == model.Horizon, cancellationToken);

        if (stored is null)
        {
            model.Id = 0;
            _db.TrainedHorizonModels.Add(model);
        }
        else
        {
            stored.TrainedAtUtc = model.TrainedAtUtc;
            stored.TrainStartDate = model.TrainStartDate;
            stored.TrainEndDate = model.TrainEndDate;
            stored.ValidationStartDate = model.ValidationStartDate;
            stored.ValidationEndDate = model.ValidationEndDate;
            stored.TrainSampleCount = model.TrainSampleCount;
            stored.ValidationSampleCount = model.ValidationSampleCount;
            stored.EmbargoBars = model.EmbargoBars;
            stored.BaselineBrierScore = model.BaselineBrierScore;
            stored.TrainedBrierScore = model.TrainedBrierScore;
            stored.BaselineLogLoss = model.BaselineLogLoss;
            stored.TrainedLogLoss = model.TrainedLogLoss;
            stored.ValidationWinRate = model.ValidationWinRate;
            stored.ValidationExpectancyR = model.ValidationExpectancyR;
            stored.ValidationProfitFactor = model.ValidationProfitFactor;
            stored.OptimalKellyFractionMultiplier = model.OptimalKellyFractionMultiplier;
            stored.Intercept = model.Intercept;
            stored.WeightsJson = model.WeightsJson;
            stored.FeatureStatsJson = model.FeatureStatsJson;
            stored.IsActive = model.IsActive;
            stored.LiveDrift = model.LiveDrift;
            stored.LiveDriftGradedPicks = model.LiveDriftGradedPicks;
        }

        await _db.SaveChangesAsync(cancellationToken);
        _db.ChangeTracker.Clear();
    }
}
