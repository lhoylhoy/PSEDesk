using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Infrastructure.Scheduling;

/// <summary>
/// Runs all four horizon pipelines (DAY, WEEK, MONTH, YEAR) after the PSE close and the
/// end-of-day settlement window. Weekends and Philippine regular holidays are skipped
/// using <see cref="PseTradingSessionRules"/>, and the loop recomputes the next fire time
/// each cycle so a missed run (sleep/suspend) simply rolls forward to the next session.
/// </summary>
public sealed class DailySignalSchedulerService : BackgroundService
{
    public static readonly TimeOnly RunAt = PseTradingSessionRules.EndOfDaySettlement;

    private static readonly SignalHorizon[] Horizons =
    {
        SignalHorizon.DAY,
        SignalHorizon.WEEK,
        SignalHorizon.MONTH,
        SignalHorizon.YEAR
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DailySignalSchedulerService> _logger;

    public DailySignalSchedulerService(
        IServiceScopeFactory scopeFactory,
        ILogger<DailySignalSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Daily PSE signal scheduler started. Fire time: {RunAt} PHT on PSE trading days, horizons: {Horizons}.",
            RunAt.ToString("HH:mm"),
            string.Join(", ", Horizons));

        // A cold desk must not wait for the first weekly slot before it prices with a learned
        // book, so the bootstrap refit is queued in the background: the scheduling loop below
        // starts immediately, and a rate-limited or offline feed simply leaves the structural
        // prior in force instead of delaying or crashing the desk.
        _ = Task.Run(() => BootstrapRetrainAsync(stoppingToken), CancellationToken.None);

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = ManilaTimeZone.NowPht();
            var nextRun = NextRun(now);
            var delay = nextRun - now;

            _logger.LogInformation(
                "Next daily PSE signal run scheduled for {NextRun:yyyy-MM-dd HH:mm} PHT ({Delay}).",
                nextRun,
                delay);

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await RunOnceAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Returns the next PSE trading day's 15:35 PHT on or after <paramref name="nowPht"/>.
    /// Weekends and Philippine holidays are skipped, so the drift chain never gains a
    /// phantom trading day.
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowPht)
    {
        var today = DateOnly.FromDateTime(nowPht.DateTime);

        if (PseTradingSessionRules.IsTradingDay(today))
        {
            var candidate = new DateTime(
                today.Year,
                today.Month,
                today.Day,
                RunAt.Hour,
                RunAt.Minute,
                0,
                DateTimeKind.Unspecified);

            var candidateOffset = new DateTimeOffset(candidate, nowPht.Offset);

            if (candidateOffset > nowPht)
            {
                return candidateOffset;
            }
        }

        var nextDay = today.AddDays(1);

        while (!PseTradingSessionRules.IsTradingDay(nextDay))
        {
            nextDay = nextDay.AddDays(1);
        }

        var next = new DateTime(nextDay.Year, nextDay.Month, nextDay.Day, RunAt.Hour, RunAt.Minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(next, nowPht.Offset);
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var runDate = ManilaTimeZone.TodayPht();

        if (!PseTradingSessionRules.IsTradingDay(runDate))
        {
            _logger.LogInformation(
                "{Date} is not a PSE trading day (weekend or Philippine holiday); skipping the scheduled run.",
                runDate);
            return;
        }

        foreach (var horizon in Horizons)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var pipeline = scope.ServiceProvider.GetRequiredService<SignalPipelineService>();

                _logger.LogInformation("Scheduled signal run starting for {Date} ({Horizon}).", runDate, horizon);
                var result = await pipeline.RunAsync(runDate, horizon, cancellationToken);

                _logger.LogInformation(
                    "Scheduled {Horizon} signal run finished for {Date}: {Picks} picks, {Drops} drops.",
                    horizon,
                    runDate,
                    result.Items.Count,
                    result.Drops.Count);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failing horizon must not block the other three: each is an
                // independent LLM run and the next attempt is the next trading day.
                _logger.LogError(
                    ex,
                    "Scheduled {Horizon} signal run failed for {Date}. The next attempt is on the next PSE trading day.",
                    horizon,
                    runDate);
            }
        }

        if (ShouldRetrainThisWeek(runDate))
        {
            await RetrainModelsAsync(runDate, cancellationToken);
        }
    }

    /// <summary>
    /// True on the first PSE trading day of the week. The learned weights are refreshed once a
    /// week, after the close, so the next session prices with a model trained only on outcomes
    /// that had already resolved.
    /// </summary>
    public static bool ShouldRetrainThisWeek(DateOnly date)
    {
        for (var offset = 1; offset <= 6; offset++)
        {
            var earlier = date.AddDays(-offset);

            if (earlier.DayOfWeek == DayOfWeek.Sunday)
            {
                break;
            }

            if (PseTradingSessionRules.IsTradingDay(earlier))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True while some horizon still has no model row at all, i.e. the desk is pricing with the
    /// structural prior because it has never been trained. A refit that ran and was rejected is
    /// a decision — <see cref="TrainedModelStatus.PriorFallback"/> — not a cold start, so it does
    /// not queue a bootstrap on every boot.
    /// </summary>
    public static bool NeedsBootstrap(IReadOnlyList<TrainedModelSummaryDto> models) =>
        models.Any(model => !model.IsActive && model.Status == TrainedModelStatus.Untrained);

    /// <summary>
    /// Fits all four books once if the desk boots cold. Failures are logged and swallowed: the
    /// structural prior stays in force and the next weekly slot tries again, so a rate-limited
    /// market feed (HTTP 429) can delay the first model but never the desk.
    /// </summary>
    private async Task BootstrapRetrainAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var training = scope.ServiceProvider.GetRequiredService<MoneyManagerTrainingService>();

            var stored = new List<TrainedModelSummaryDto>(Horizons.Length);

            foreach (var horizon in Horizons)
            {
                stored.Add(await training.GetModelAsync(horizon, cancellationToken));
            }

            if (!NeedsBootstrap(stored))
            {
                _logger.LogInformation(
                    "Bootstrap money-manager refit skipped: every horizon already carries a fitted model ({Statuses}).",
                    string.Join(", ", stored.Select(model => $"{model.Horizon} {model.Status}")));
                return;
            }

            var runDate = ManilaTimeZone.TodayPht();
            _logger.LogInformation(
                "Bootstrap money-manager refit starting for {Date}: {Horizons} still price with the structural prior.",
                runDate,
                string.Join(", ", stored.Where(model => model.Status == TrainedModelStatus.Untrained).Select(model => model.Horizon)));

            var summaries = await training.TrainAllAsync(runDate, null, cancellationToken);

            _logger.LogInformation(
                "Bootstrap money-manager refit finished for {Date}: {Summary}.",
                runDate,
                string.Join(", ", summaries.Select(summary => $"{summary.Horizon} {summary.Status}")));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // The desk stays up with the structural prior: a bootstrap is an optimisation, not a
            // prerequisite for pricing a pick.
            _logger.LogWarning(
                ex,
                "Bootstrap money-manager refit failed. The structural prior stays in force until the next weekly refit.");
        }
    }

    /// <summary>
    /// Re-fits all four books from the real tape. A training failure never stops the desk: the
    /// stored weights simply stay in force and the log says why.
    /// </summary>
    private async Task RetrainModelsAsync(DateOnly runDate, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var training = scope.ServiceProvider.GetRequiredService<MoneyManagerTrainingService>();

            var summaries = await training.TrainAllAsync(runDate, null, cancellationToken);

            _logger.LogInformation(
                "Weekly money-manager retrain finished for {Date}: {Summary}.",
                runDate,
                string.Join(", ", summaries.Select(summary => $"{summary.Horizon} {summary.Status}")));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Weekly money-manager retrain failed for {Date}. The stored weights stay in force.",
                runDate);
        }
    }
}
