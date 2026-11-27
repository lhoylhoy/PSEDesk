using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Tests.Application;

public sealed class SignalOutcomeServiceTests
{
    private static readonly DateOnly RunDate = new(2026, 11, 23);

    [Fact]
    public void ResolveItem_grades_a_target_touch_as_a_fee_adjusted_win()
    {
        var item = BuildItem();
        var forward = new List<StockDailyMetric>
        {
            // The entry is a limit order: the first session only counts once its low trades
            // through PHP 100.
            BuildSession(RunDate.AddDays(1), close: 101m, high: 101.5m, low: 99.8m),
            BuildSession(RunDate.AddDays(2), close: 110m, high: 111m, low: 105m)
        };

        var resolved = SignalOutcomeService.ResolveItem(item, forward, forwardWindowBars: 10);

        Assert.True(resolved);
        Assert.Equal(SignalOutcomeStatus.WIN_TARGET, item.OutcomeStatus);
        Assert.Equal(RunDate.AddDays(2), item.OutcomeDate);
        Assert.Equal(110m, item.OutcomeExitPrice);
        Assert.Equal(2, item.BarsHeld);

        // PHP 10.00 of gain less the 0.295% buy and 0.395% sell legs, over the PHP 5.67025
        // of fee-paid risk: 1.6349R, not the 1.2039R a symmetric 0.895% model printed.
        Assert.Equal(1.6349m, item.RealizedNetR);
    }

    [Fact]
    public void ResolveItem_treats_a_same_session_stop_and_target_as_a_stop_out()
    {
        var item = BuildItem();
        var forward = new List<StockDailyMetric>
        {
            BuildSession(RunDate.AddDays(1), close: 102m, high: 112m, low: 94m)
        };

        var resolved = SignalOutcomeService.ResolveItem(item, forward, forwardWindowBars: 10);

        Assert.True(resolved);
        Assert.Equal(SignalOutcomeStatus.LOSS_STOP, item.OutcomeStatus);
        Assert.Equal(-1.0m, item.RealizedNetR);
        Assert.Equal(1, item.BarsHeld);
    }

    [Fact]
    public void ResolveItem_marks_the_expiry_close_when_neither_barrier_is_hit()
    {
        var item = BuildItem();
        var forward = Enumerable.Range(1, 3)
            .Select(offset => BuildSession(RunDate.AddDays(offset), close: 101m, high: 101.5m, low: 99.8m))
            .ToList();

        var resolved = SignalOutcomeService.ResolveItem(item, forward, forwardWindowBars: 3);

        Assert.True(resolved);
        Assert.Equal(SignalOutcomeStatus.EXPIRED_WIN, item.OutcomeStatus);
        Assert.Equal(101m, item.OutcomeExitPrice);
        Assert.Equal(3, item.BarsHeld);

        // A PHP 1.00 move pays the 0.295% buy and 0.395% sell legs and still nets 0.0540R.
        Assert.Equal(0.054m, item.RealizedNetR);
    }

    [Fact]
    public void ResolveItem_marks_a_horizon_that_closed_underwater_as_an_expired_loss()
    {
        var item = BuildItem();
        var forward = Enumerable.Range(1, 3)
            .Select(offset => BuildSession(RunDate.AddDays(offset), close: 100.2m, high: 100.5m, low: 99.8m))
            .ToList();

        var resolved = SignalOutcomeService.ResolveItem(item, forward, forwardWindowBars: 3);

        Assert.True(resolved);
        Assert.Equal(SignalOutcomeStatus.EXPIRED_LOSS, item.OutcomeStatus);
        Assert.Equal(100.2m, item.OutcomeExitPrice);

        // PHP 0.20 of gain cannot pay the PHP 0.69 round trip: -0.0866R.
        Assert.Equal(-0.0866m, item.RealizedNetR);
    }

    [Fact]
    public void ResolveItem_never_grades_an_entry_the_tape_never_traded_at()
    {
        var item = BuildItem();

        // Every session in the window stayed above the PHP 100 limit, so the desk never owned
        // the name. Grading that as a win or a loss would book a P&L for a position that did
        // not exist.
        var forward = Enumerable.Range(1, 3)
            .Select(offset => BuildSession(RunDate.AddDays(offset), close: 101m, high: 101.5m, low: 100.5m))
            .ToList();

        var resolved = SignalOutcomeService.ResolveItem(item, forward, forwardWindowBars: 3);

        Assert.True(resolved);
        Assert.Equal(SignalOutcomeStatus.UNFILLED, item.OutcomeStatus);
        Assert.Null(item.OutcomeExitPrice);
        Assert.Null(item.RealizedNetR);
        Assert.Equal(3, item.BarsHeld);
    }

    [Fact]
    public void ResolveItem_leaves_a_pick_open_while_the_forward_window_is_still_filling()
    {
        var item = BuildItem();
        var forward = new List<StockDailyMetric>
        {
            BuildSession(RunDate.AddDays(1), close: 101m, high: 101.5m, low: 99.8m),
            BuildSession(RunDate.AddDays(2), close: 101m, high: 101.5m, low: 100.6m)
        };

        var resolved = SignalOutcomeService.ResolveItem(item, forward, forwardWindowBars: 3);

        Assert.False(resolved);
        Assert.Equal(SignalOutcomeStatus.OPEN, item.OutcomeStatus);
        Assert.Null(item.RealizedNetR);
    }

    [Fact]
    public async Task ResolveOpenSignalsAsync_persists_only_the_picks_it_could_grade()
    {
        var graded = BuildItem();
        graded.Id = 42;

        // Both picks are the same ticker on purpose: the grading window is per ticker, so
        // one stored series must be able to grade every stored item.
        var second = BuildItem();
        second.Id = 43;

        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem> { graded, second }
        });

        repository.Metrics.Add(BuildSession(RunDate.AddDays(1), close: 101m, high: 101.5m, low: 99.8m));
        repository.Metrics.Add(BuildSession(RunDate.AddDays(2), close: 111m, high: 112m, low: 105m));

        var service = new SignalOutcomeService(repository);

        var resolvedCount = await service.ResolveOpenSignalsAsync(SignalHorizon.WEEK);

        Assert.Equal(2, resolvedCount);
        Assert.Equal(new[] { 42, 43 }, repository.Updated.Select(item => item.Id).OrderBy(id => id));
        Assert.All(repository.Updated, item => Assert.Equal(SignalOutcomeStatus.WIN_TARGET, item.OutcomeStatus));
    }

    [Fact]
    public async Task BuildScorecardAsync_reports_expectancy_calibration_and_the_verdict_split()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem>
            {
                BuildGradedItem("WINNER", 0.60m, SignalOutcomeStatus.WIN_TARGET, 1.94m, EdgeVerdict.TRADE, RunDate.AddDays(2)),
                BuildGradedItem("LOSER", 0.40m, SignalOutcomeStatus.LOSS_STOP, -1m, EdgeVerdict.SMALL_SIZE, RunDate.AddDays(5)),
                BuildItem()
            }
        });

        var scorecard = await new SignalOutcomeService(repository).BuildScorecardAsync(SignalHorizon.WEEK);

        Assert.Equal(3, scorecard.TotalPicks);
        Assert.Equal(2, scorecard.ResolvedCount);
        Assert.Equal(1, scorecard.OpenCount);
        Assert.Equal(0.5m, scorecard.WinRate);
        Assert.Equal(0.47m, scorecard.AverageRealizedNetR);
        Assert.Equal(0.47m, scorecard.ExpectancyR);
        Assert.Equal(1.94m, scorecard.ProfitFactor);

        // (0.60 - 1)^2 and (0.40 - 0)^2 both equal 0.16.
        Assert.Equal(0.16m, scorecard.BrierScore);
        Assert.Equal(2, scorecard.Calibration.Count);
        Assert.Equal("40-60%", scorecard.Calibration[0].Label);
        Assert.Equal(0m, scorecard.Calibration[0].ActualWinPct);
        Assert.Equal("60-80%", scorecard.Calibration[1].Label);
        Assert.Equal(100m, scorecard.Calibration[1].ActualWinPct);

        var trade = scorecard.Verdicts.Single(verdict => verdict.Verdict == "TRADE");
        Assert.Equal(1, trade.ResolvedCount);
        Assert.Equal(1.94m, trade.TotalRealizedNetR);

        var watchOnly = scorecard.Verdicts.Single(verdict => verdict.Verdict == "WATCH_ONLY");
        Assert.Equal(1, watchOnly.Count);
        Assert.Equal(0, watchOnly.ResolvedCount);

        Assert.Equal("LOSER", scorecard.RecentResolved[0].Ticker);
    }

    [Fact]
    public async Task BuildScorecardAsync_keeps_rows_without_a_stored_forecast_out_of_the_calibration()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem>
            {
                BuildGradedItem("FORECAST", 0.60m, SignalOutcomeStatus.WIN_TARGET, 1.94m, EdgeVerdict.TRADE, RunDate.AddDays(2)),
                // Pre-migration row: graded for real, but it never carried a probability.
                BuildGradedItem("LEGACY", 0m, SignalOutcomeStatus.WIN_TARGET, 1.94m, EdgeVerdict.WATCH_ONLY, RunDate.AddDays(3))
            }
        });

        var scorecard = await new SignalOutcomeService(repository).BuildScorecardAsync(SignalHorizon.WEEK);

        Assert.Equal(2, scorecard.ResolvedCount);
        Assert.Equal(1m, scorecard.WinRate);
        Assert.Equal(1.94m, scorecard.ExpectancyR);

        // Only the forecasted pick is graded against its probability: (0.60 - 1)^2 = 0.16.
        Assert.Equal(0.16m, scorecard.BrierScore);
        var bucket = Assert.Single(scorecard.Calibration);
        Assert.Equal("60-80%", bucket.Label);
        Assert.Equal(60m, bucket.PredictedWinPct);
        Assert.Equal(100m, bucket.ActualWinPct);
        Assert.Equal(1, bucket.SampleCount);
        Assert.Equal(2, scorecard.RecentResolved.Count);
    }

    [Fact]
    public async Task BuildScorecardAsync_keeps_an_unfilled_pick_out_of_the_ledger()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem>
            {
                BuildGradedItem("WINNER", 0.60m, SignalOutcomeStatus.WIN_TARGET, 1.94m, EdgeVerdict.TRADE, RunDate.AddDays(2)),
                BuildGradedItem("LOSER", 0.40m, SignalOutcomeStatus.LOSS_STOP, -1m, EdgeVerdict.SMALL_SIZE, RunDate.AddDays(5)),
                // The limit never traded, so the desk never owned the name and has no P&L to book.
                BuildUnfilledItem("NEVER", 0.55m, EdgeVerdict.TRADE, RunDate.AddDays(3))
            }
        });

        var scorecard = await new SignalOutcomeService(repository).BuildScorecardAsync(SignalHorizon.WEEK);

        // Still a published pick, but neither graded nor open: a third resolved row (or a phantom
        // open position) would dilute every ratio below.
        Assert.Equal(3, scorecard.TotalPicks);
        Assert.Equal(2, scorecard.ResolvedCount);
        Assert.Equal(0, scorecard.OpenCount);
        Assert.Equal(0.5m, scorecard.WinRate);
        Assert.Equal(0.47m, scorecard.ExpectancyR);
        Assert.Equal(1.94m, scorecard.ProfitFactor);

        // A zero R would have been graded as a 45-point miss on the 0.55 forecast.
        Assert.Equal(0.16m, scorecard.BrierScore);
        Assert.Equal(2, scorecard.Calibration.Sum(bucket => bucket.SampleCount));

        Assert.Equal(2, scorecard.RecentResolved.Count);
        Assert.DoesNotContain(
            scorecard.RecentResolved,
            pick => pick.OutcomeStatus == nameof(SignalOutcomeStatus.UNFILLED));
        Assert.All(scorecard.RecentResolved, pick => Assert.NotNull(pick.RealizedNetR));

        // The verdict split counts the pick (it was published as a TRADE) without crediting it.
        var trade = scorecard.Verdicts.Single(verdict => verdict.Verdict == "TRADE");
        Assert.Equal(2, trade.Count);
        Assert.Equal(1, trade.ResolvedCount);
        Assert.Equal(1.94m, trade.TotalRealizedNetR);
    }

    [Fact]
    public async Task ResolveOpenSignalsAsync_never_regrades_a_pick_the_tape_left_unfilled()
    {
        var item = BuildItem();
        item.Id = 7;

        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem> { item }
        });

        // A full WEEK window (10 bars) that never traded down to the PHP 100 limit.
        foreach (var offset in Enumerable.Range(1, 10))
        {
            repository.Metrics.Add(
                BuildSession(RunDate.AddDays(offset), close: 101m, high: 101.5m, low: 100.5m));
        }

        var service = new SignalOutcomeService(repository);

        Assert.Equal(1, await service.ResolveOpenSignalsAsync(SignalHorizon.WEEK));
        Assert.Equal(SignalOutcomeStatus.UNFILLED, item.OutcomeStatus);

        // UNFILLED is terminal: the next pass has nothing left to grade and must not invent a
        // realized R or re-book the item.
        Assert.Equal(0, await service.ResolveOpenSignalsAsync(SignalHorizon.WEEK));
        Assert.Single(repository.Updated);
        Assert.Null(item.RealizedNetR);
        Assert.Null(item.OutcomeExitPrice);
    }

    [Fact]
    public async Task ComputeLiveCalibrationOffsetPctAsync_stays_at_zero_while_the_sample_is_too_thin()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = Enumerable.Range(0, 5)
                .Select(index => BuildGradedItem(
                    "ICT",
                    0.55m,
                    SignalOutcomeStatus.WIN_TARGET,
                    1.94m,
                    EdgeVerdict.TRADE,
                    RunDate.AddDays(index + 1)))
                .ToList()
        });

        var service = new SignalOutcomeService(repository);

        Assert.Equal(0m, await service.ComputeLiveCalibrationOffsetPctAsync(SignalHorizon.WEEK));
    }

    [Fact]
    public async Task ComputeLiveCalibrationOffsetPctAsync_caps_a_hopelessly_under_forecast_ledger()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = Enumerable.Range(0, 20)
                .Select(index => BuildGradedItem(
                    "ICT",
                    0.55m,
                    SignalOutcomeStatus.WIN_TARGET,
                    1.94m,
                    EdgeVerdict.TRADE,
                    RunDate.AddDays(index + 1)))
                .ToList()
        });

        var service = new SignalOutcomeService(repository);

        // 20 wins at a claimed 55% is a +45 point miss, but the offset may never exceed 10.
        Assert.Equal(10m, await service.ComputeLiveCalibrationOffsetPctAsync(SignalHorizon.WEEK));
    }

    [Fact]
    public async Task ComputeLiveCalibrationOffsetPctAsync_shrinks_a_modest_miss_toward_the_prior()
    {
        var repository = new FakeSignalRepository();
        var items = Enumerable.Range(0, 25)
            .Select(index => BuildGradedItem(
                "ICT",
                0.50m,
                index < 10 ? SignalOutcomeStatus.WIN_TARGET : SignalOutcomeStatus.LOSS_STOP,
                index < 10 ? 1.94m : -1m,
                EdgeVerdict.SMALL_SIZE,
                RunDate.AddDays(index + 1)))
            .ToList();

        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = items
        });

        var service = new SignalOutcomeService(repository);

        // 40% realized against 50% claimed is a -10 point miss at 25 samples, and the
        // 20 pseudo-samples shrink the applied offset to 25/45 of that.
        Assert.Equal(-5.56m, await service.ComputeLiveCalibrationOffsetPctAsync(SignalHorizon.WEEK));
    }

    [Fact]
    public async Task BuildDeskMemoryAsync_reports_the_realized_ledger_and_its_stop_outs()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem>
            {
                BuildGradedItem("ICT", 0.60m, SignalOutcomeStatus.WIN_TARGET, 1.94m, EdgeVerdict.TRADE, RunDate.AddDays(2)),
                BuildGradedItem("ICT", 0.40m, SignalOutcomeStatus.LOSS_STOP, -1m, EdgeVerdict.SMALL_SIZE, RunDate.AddDays(5)),
                BuildItem()
            }
        });
        repository.Metrics.Add(BuildSession(RunDate, close: 100m, high: 101m, low: 99m));

        var service = new SignalOutcomeService(repository);

        var memory = await service.BuildDeskMemoryAsync(SignalHorizon.WEEK);

        Assert.Equal("WEEK", memory.Horizon);
        Assert.Equal(2, memory.ResolvedCount);
        Assert.Equal(0.5m, memory.WinRate);
        Assert.Equal(0.47m, memory.ExpectancyR);
        Assert.Equal(0.16m, memory.BrierScore);
        Assert.Equal(0m, memory.CalibrationBiasPct);

        var sector = Assert.Single(memory.BestSectors);
        Assert.Equal("Industrial", sector.Sector);
        Assert.Equal(2, sector.Samples);
        Assert.Equal(0.47m, sector.AverageRealizedNetR);

        var stopOut = Assert.Single(memory.RecentStopOuts);
        Assert.Equal(-1m, stopOut.RealizedNetR);
        Assert.Equal(3, stopOut.BarsHeld);

        // No promoted model exists in this ledger, so no factor can claim to be learned.
        Assert.Empty(memory.TopFactors);
    }

    [Fact]
    public async Task BuildDeskMemoryAsync_is_empty_and_honest_before_the_first_pick()
    {
        var memory = await new SignalOutcomeService(new FakeSignalRepository())
            .BuildDeskMemoryAsync(SignalHorizon.MONTH);

        Assert.Equal("MONTH", memory.Horizon);
        Assert.Equal(0, memory.ResolvedCount);
        Assert.Empty(memory.BestSectors);
        Assert.Empty(memory.WorstSectors);
        Assert.Empty(memory.RecentStopOuts);
    }

    [Fact]
    public async Task EvaluateLiveDriftAsync_halts_a_promoted_book_whose_rolling_expectancy_turned_negative()
    {
        var repository = new FakeSignalRepository();
        repository.TrainedModel = BuildActiveModel();
        repository.Runs.Add(BuildRunWithLosses(15));

        var drift = await new SignalOutcomeService(repository).EvaluateLiveDriftAsync(SignalHorizon.WEEK);

        Assert.Equal(LiveDriftStatus.HALTED, drift.Status);
        Assert.Equal(15, drift.GradedPicks);
        Assert.Equal(0m, drift.RealizedWinRate);
        Assert.Equal(0.50m, drift.ExpectedWinRate);
        Assert.Equal(-1m, drift.RollingExpectancyR);
        Assert.Equal(0.60m, drift.BaselineExpectancyR);

        // Positive gaps mean the live book is behind the record it was promoted on.
        Assert.Equal(50m, drift.WinRateGapPct);
        Assert.Equal(1.60m, drift.ExpectancyGapR);
        Assert.Contains("HALTED", drift.Reason, StringComparison.Ordinal);

        // The verdict is persisted, because the release hysteresis has to survive a restart.
        var saved = Assert.Single(repository.SavedModels);
        Assert.Equal(LiveDriftStatus.HALTED, saved.LiveDrift);
        Assert.Equal(15, saved.LiveDriftGradedPicks);
    }

    [Fact]
    public async Task EvaluateLiveDriftAsync_leaves_a_prior_priced_book_unthrottled()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(BuildRunWithLosses(15));

        var drift = await new SignalOutcomeService(repository).EvaluateLiveDriftAsync(SignalHorizon.WEEK);

        // No promoted model means no promoted record to drift from: the prior is priced by the
        // structural odds and corrected by the calibration offset, so nothing is throttled.
        Assert.Equal(LiveDriftStatus.STABLE, drift.Status);
        Assert.Equal(15, drift.GradedPicks);
        Assert.Null(drift.Reason);
        Assert.Empty(repository.SavedModels);
    }

    [Fact]
    public async Task EvaluateLiveDriftAsync_reads_only_the_most_recent_graded_window()
    {
        var repository = new FakeSignalRepository();
        repository.TrainedModel = BuildActiveModel();
        repository.Runs.Add(BuildRunWithLosses(40));

        var drift = await new SignalOutcomeService(repository).EvaluateLiveDriftAsync(SignalHorizon.WEEK);

        Assert.Equal(LiveDriftSnapshot.WindowPicks, drift.GradedPicks);
        Assert.Equal(LiveDriftStatus.HALTED, drift.Status);
    }

    [Fact]
    public async Task BuildScorecardAsync_reports_a_perfect_rank_agreement_when_the_model_follows_the_screen()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem>
            {
                BuildQuantItem("AAA", rank: 1, quantRank: 1, SignalOutcomeStatus.WIN_TARGET, 1.5m),
                BuildQuantItem("BBB", rank: 2, quantRank: 2, SignalOutcomeStatus.LOSS_STOP, -1m),
                BuildQuantItem("CCC", rank: 3, quantRank: 3, SignalOutcomeStatus.WIN_TARGET, 2m)
            }
        });

        var scorecard = await new SignalOutcomeService(repository).BuildScorecardAsync(SignalHorizon.WEEK);

        var quality = scorecard.RankingQuality;
        Assert.NotNull(quality);
        Assert.Equal(1, quality!.RunsMeasured);
        Assert.Equal(3, quality.PicksWithQuantRank);
        Assert.Equal(1m, quality.MeanSpearmanRho);
        Assert.Equal(3, quality.ModelTop3Resolved);
        Assert.Equal(3, quality.QuantTop3Resolved);
        Assert.Equal(0.6667m, quality.ModelTop3WinRate);
        Assert.Equal(0.8333m, quality.ModelTop3AverageRealizedNetR);
    }

    [Fact]
    public async Task BuildScorecardAsync_shows_when_the_models_top_three_beat_the_screens_top_three()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem>
            {
                // The model promoted AAA and CCC, which are mid-table in the quant ordering,
                // and pushed the screen's second-best name (DDD) to the bottom.
                BuildQuantItem("AAA", rank: 1, quantRank: 5, SignalOutcomeStatus.WIN_TARGET, 1.5m),
                BuildQuantItem("BBB", rank: 2, quantRank: 1, SignalOutcomeStatus.LOSS_STOP, -1m),
                BuildQuantItem("CCC", rank: 3, quantRank: 6, SignalOutcomeStatus.WIN_TARGET, 2m),
                BuildQuantItem("DDD", rank: 4, quantRank: 2, SignalOutcomeStatus.LOSS_STOP, -1m)
            }
        });

        var scorecard = await new SignalOutcomeService(repository).BuildScorecardAsync(SignalHorizon.WEEK);

        var quality = scorecard.RankingQuality;
        Assert.NotNull(quality);
        Assert.Equal(0m, quality!.MeanSpearmanRho);
        Assert.Equal(3, quality.ModelTop3Resolved);
        Assert.Equal(0.6667m, quality.ModelTop3WinRate);
        Assert.Equal(0.8333m, quality.ModelTop3AverageRealizedNetR);

        // Quant's top three of the names the model actually picked: BBB and DDD only — neither
        // of the screen's best two names paid, so the screen's own top three would have lost.
        Assert.Equal(2, quality.QuantTop3Resolved);
        Assert.Equal(0m, quality.QuantTop3WinRate);
        Assert.Equal(-1m, quality.QuantTop3AverageRealizedNetR);
    }

    [Fact]
    public async Task BuildScorecardAsync_omits_the_ranking_read_when_no_run_stored_a_quant_rank()
    {
        var repository = new FakeSignalRepository();
        repository.Runs.Add(new SignalRun
        {
            Id = 1,
            RunDate = RunDate,
            Horizon = SignalHorizon.WEEK,
            CreatedAtUtc = DateTime.UtcNow,
            Items = new List<SignalItem>
            {
                BuildGradedItem("ICT", 0.55m, SignalOutcomeStatus.WIN_TARGET, 1.5m, EdgeVerdict.TRADE, RunDate.AddDays(3))
            }
        });

        var scorecard = await new SignalOutcomeService(repository).BuildScorecardAsync(SignalHorizon.WEEK);

        Assert.Null(scorecard.RankingQuality);
    }

    private static SignalItem BuildQuantItem(
        string ticker,
        int rank,
        int quantRank,
        SignalOutcomeStatus status,
        decimal realizedNetR)
    {
        var item = BuildGradedItem(ticker, 0.55m, status, realizedNetR, EdgeVerdict.TRADE, RunDate.AddDays(3));
        item.Rank = rank;
        item.QuantRank = quantRank;
        item.QuantPreScore = 100 - quantRank;
        return item;
    }

    private static TrainedHorizonModel BuildActiveModel() => new()
    {
        Horizon = SignalHorizon.WEEK,
        TrainedAtUtc = DateTime.UtcNow,
        ValidationWinRate = 0.50m,
        ValidationExpectancyR = 0.60m,
        IsActive = true,
        LiveDrift = LiveDriftStatus.STABLE
    };

    private static SignalRun BuildRunWithLosses(int count) => new()
    {
        Id = 1,
        RunDate = RunDate,
        Horizon = SignalHorizon.WEEK,
        CreatedAtUtc = DateTime.UtcNow,
        Items = Enumerable.Range(0, count)
            .Select(index => BuildGradedItem(
                $"LOSER{index}",
                0.5m,
                SignalOutcomeStatus.LOSS_STOP,
                -1m,
                EdgeVerdict.TRADE,
                RunDate.AddDays(index + 1)))
            .ToList()
    };

    private static SignalItem BuildItem() =>
        new()
        {
            Ticker = "ICT",
            Rank = 1,
            ConvictionScore = 80,
            EntryPrice = 100m,
            StopLoss = 95m,
            TargetPrice = 110m,
            RiskRewardRatio = 2m,
            Thesis = "Breakout on volume.",
            DeviationReason = string.Empty
        };

    private static SignalItem BuildGradedItem(
        string ticker,
        decimal winProbability,
        SignalOutcomeStatus status,
        decimal realizedNetR,
        EdgeVerdict verdict,
        DateOnly outcomeDate)
    {
        var item = BuildItem();
        item.Ticker = ticker;
        item.WinProbability = winProbability;
        item.OutcomeStatus = status;
        item.RealizedNetR = realizedNetR;
        item.OutcomeDate = outcomeDate;
        item.OutcomeExitPrice = status == SignalOutcomeStatus.WIN_TARGET ? 110m : 95m;
        item.BarsHeld = 3;
        item.EdgeVerdict = verdict;
        return item;
    }

    /// <summary>
    /// A pick the tape never filled: the row exists and was published, but it carries no exit
    /// price and no realized R because no position was ever opened.
    /// </summary>
    private static SignalItem BuildUnfilledItem(
        string ticker,
        decimal winProbability,
        EdgeVerdict verdict,
        DateOnly outcomeDate)
    {
        var item = BuildItem();
        item.Ticker = ticker;
        item.WinProbability = winProbability;
        item.OutcomeStatus = SignalOutcomeStatus.UNFILLED;
        item.OutcomeDate = outcomeDate;
        item.OutcomeExitPrice = null;
        item.RealizedNetR = null;
        item.BarsHeld = 10;
        item.EdgeVerdict = verdict;
        return item;
    }

    private static StockDailyMetric BuildSession(DateOnly tradeDate, decimal close, decimal high, decimal low) =>
        new()
        {
            TradeDate = tradeDate,
            Ticker = "ICT",
            CompanyName = "International Container Terminal Services, Inc.",
            Sector = "Industrial",
            ClosePrice = close,
            HighPrice = high,
            LowPrice = low,
            OpenPrice = close,
            ChangePct = 0.5m,
            AvgTurnover20dPhp = 250_000_000m,
            VolumeRatioVs20d = 1.2m,
            Rsi14 = 58m,
            Ema20 = close - 2m,
            Sma50 = close - 5m,
            Sma200 = close - 10m,
            Atr14 = 3m,
            SupportLevel = close - 4m,
            ResistanceLevel = close + 6m,
            QuantPreScore = 80
        };

    private sealed class FakeSignalRepository : ISignalRepository
    {
        public List<SignalRun> Runs { get; } = new();

        public List<StockDailyMetric> Metrics { get; } = new();

        public List<SignalItem> Updated { get; } = new();

        public TrainedHorizonModel? TrainedModel { get; set; }

        public List<TrainedHorizonModel> SavedModels { get; } = new();

        public Task<TrainedHorizonModel?> GetTrainedModelAsync(
            SignalHorizon horizon,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                TrainedModel is not null && TrainedModel.Horizon == horizon ? TrainedModel : null);

        public Task SaveTrainedModelAsync(
            TrainedHorizonModel model,
            CancellationToken cancellationToken = default)
        {
            SavedModels.Add(model);
            TrainedModel = model;
            return Task.CompletedTask;
        }

        public Task<DailyMarketSnapshot?> GetSnapshotAsync(
            DateOnly tradeDate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<SignalRun> runs = Runs
                .Where(run => horizon is null || run.Horizon == horizon)
                .ToList();

            return Task.FromResult(runs);
        }

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsRangeAsync(
            DateOnly fromDate,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<StockDailyMetric> metrics = Metrics
                .Where(metric => metric.TradeDate >= fromDate)
                .OrderBy(metric => metric.TradeDate)
                .ToList();

            return Task.FromResult(metrics);
        }

        public Task UpdateResolvedOutcomesAsync(
            IReadOnlyList<SignalItem> items,
            CancellationToken cancellationToken = default)
        {
            Updated.AddRange(items);
            return Task.CompletedTask;
        }

        public Task SaveMarketDataAsync(
            MarketDataBatch batch,
            IReadOnlyList<StockDailyMetric> metrics,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSignalRunAsync(SignalRun run, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
