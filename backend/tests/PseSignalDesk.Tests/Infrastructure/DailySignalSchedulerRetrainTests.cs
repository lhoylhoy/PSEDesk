using PseSignalDesk.Application.Models;
using PseSignalDesk.Infrastructure.Scheduling;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The weekly refit has to land on the first session the tape actually trades, otherwise a
/// Monday holiday silently skips the week and the live picks keep pricing with stale weights.
/// A cold boot must also fit the books without waiting for that slot, but only while a horizon
/// has never been trained — a rejected refit is a decision, not a cold start.
/// </summary>
public sealed class DailySignalSchedulerRetrainTests
{
    [Fact]
    public void NeedsBootstrap_true_while_any_horizon_has_never_been_trained()
    {
        var models = new[]
        {
            Model("DAY", TrainedModelStatus.Learned, isActive: true),
            Model("WEEK", TrainedModelStatus.Untrained, isActive: false),
            Model("MONTH", TrainedModelStatus.Learned, isActive: true),
            Model("YEAR", TrainedModelStatus.Learned, isActive: true)
        };

        Assert.True(DailySignalSchedulerService.NeedsBootstrap(models));
    }

    [Fact]
    public void NeedsBootstrap_false_once_every_horizon_carries_a_fitted_model()
    {
        var models = new[]
        {
            Model("DAY", TrainedModelStatus.Learned, isActive: true),
            Model("WEEK", TrainedModelStatus.PriorFallback, isActive: false),
            Model("MONTH", TrainedModelStatus.Learned, isActive: true),
            Model("YEAR", TrainedModelStatus.PriorFallback, isActive: false)
        };

        // PRIOR_FALLBACK means the refit ran and the safety gate rejected it; retraining on every
        // boot would only burn the market feed to reach the same verdict.
        Assert.False(DailySignalSchedulerService.NeedsBootstrap(models));
    }

    private static TrainedModelSummaryDto Model(string horizon, string status, bool isActive) =>
        TrainedModelPresenter.ToDto(null, horizon) with
        {
            Status = status,
            IsActive = isActive
        };

    [Fact]
    public void ShouldRetrainThisWeek_true_on_the_first_session_after_the_weekend()
    {
        // Monday 2026-11-23 has no PH holiday and no earlier session in its week.
        Assert.True(DailySignalSchedulerService.ShouldRetrainThisWeek(new DateOnly(2026, 11, 23)));
    }

    [Fact]
    public void ShouldRetrainThisWeek_false_once_the_week_has_already_traded()
    {
        Assert.False(DailySignalSchedulerService.ShouldRetrainThisWeek(new DateOnly(2026, 11, 24)));
        Assert.False(DailySignalSchedulerService.ShouldRetrainThisWeek(new DateOnly(2026, 11, 27)));
    }

    [Fact]
    public void ShouldRetrainThisWeek_still_fires_when_monday_is_a_holiday()
    {
        // National Heroes Day 2026 falls on Monday 2026-08-31, so the week has no earlier
        // session and the first tick that trades — Tuesday — must run the refit.
        Assert.True(DailySignalSchedulerService.ShouldRetrainThisWeek(new DateOnly(2026, 9, 1)));

        // Wednesday sees Tuesday behind it, so the book must not refit twice in one week.
        Assert.False(DailySignalSchedulerService.ShouldRetrainThisWeek(new DateOnly(2026, 9, 2)));
        Assert.False(DailySignalSchedulerService.ShouldRetrainThisWeek(new DateOnly(2026, 9, 4)));
    }
}
