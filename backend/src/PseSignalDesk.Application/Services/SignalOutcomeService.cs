using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Forward-outcome ledger. It grades every open pick against the real OHLC sessions
/// that followed its run date, then aggregates those grades into a calibration
/// scorecard. Nothing here is model-authored: the scorecard is the desk grading itself.
/// </summary>
public sealed class SignalOutcomeService
{
    private readonly ISignalRepository _repository;

    public SignalOutcomeService(ISignalRepository repository)
    {
        _repository = repository;
    }

    /// <summary>
    /// Resolves every open pick that now has enough forward history. Called after fresh
    /// market data is persisted, so today's session grades yesterday's and last week's
    /// picks without a separate job.
    /// </summary>
    public async Task<int> ResolveOpenSignalsAsync(
        SignalHorizon? horizon = null,
        CancellationToken cancellationToken = default)
    {
        var runs = await _repository.GetAllRunsAsync(horizon, cancellationToken);
        var open = runs
            .SelectMany(run => run.Items
                .Where(item => item.OutcomeStatus == SignalOutcomeStatus.OPEN)
                .Select(item => (Run: run, Item: item)))
            .ToList();

        if (open.Count == 0)
        {
            return 0;
        }

        var earliest = open.Min(entry => entry.Run.RunDate);
        var metrics = await _repository.GetMetricsRangeAsync(earliest, cancellationToken);

        var seriesByTicker = metrics
            .GroupBy(metric => metric.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<StockDailyMetric>)group.OrderBy(metric => metric.TradeDate).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var resolved = new List<SignalItem>();

        foreach (var (run, item) in open)
        {
            if (!seriesByTicker.TryGetValue(item.Ticker, out var series))
            {
                continue;
            }

            var forward = series
                .Where(metric => metric.TradeDate > run.RunDate)
                .ToList();

            var window = HorizonProfile.For(run.Horizon).ForwardWindowBars;

            if (ResolveItem(item, forward, window))
            {
                resolved.Add(item);
            }
        }

        if (resolved.Count == 0)
        {
            return 0;
        }

        await _repository.UpdateResolvedOutcomesAsync(resolved, cancellationToken);
        return resolved.Count;
    }

    /// <summary>
    /// Grades one pick against its forward sessions. Returns true when the item changed.
    /// The entry is a limit order, so nothing is graded until the tape actually trades at or
    /// through it. Once filled, the stop is checked before the target on every bar: with daily
    /// OHLC a session that touched both cannot be ordered, and the conservative accounting
    /// assumes the loss filled first.
    /// </summary>
    public static bool ResolveItem(
        SignalItem item,
        IReadOnlyList<StockDailyMetric> forwardSessions,
        int forwardWindowBars)
    {
        if (item.TargetPrice <= item.EntryPrice ||
            item.StopLoss >= item.EntryPrice ||
            forwardSessions.Count == 0 ||
            forwardWindowBars <= 0)
        {
            return false;
        }

        var netRiskPerShare = PseBoardLotService.ComputeNetRiskPerShare(item.EntryPrice, item.StopLoss);
        if (netRiskPerShare <= 0m)
        {
            return false;
        }

        var window = forwardSessions
            .OrderBy(metric => metric.TradeDate)
            .Take(forwardWindowBars)
            .ToList();

        // No fill, no position: the pick is only live from the session whose range reached the
        // entry price.
        var entryIndex = -1;
        for (var index = 0; index < window.Count; index++)
        {
            var sessionLow = window[index].LowPrice ?? window[index].ClosePrice;

            if (sessionLow <= item.EntryPrice)
            {
                entryIndex = index;
                break;
            }
        }

        if (entryIndex < 0)
        {
            // The order is still working while the horizon window is open.
            if (window.Count < forwardWindowBars)
            {
                return false;
            }

            // The horizon elapsed without a print at the entry price: nothing was bought, so
            // there is no realized R to book. Counting it as a loss would poison the ledger.
            ApplyResolution(
                item,
                SignalOutcomeStatus.UNFILLED,
                window[^1].TradeDate,
                exitPrice: null,
                realizedNetR: null,
                barsHeld: window.Count);

            return true;
        }

        for (var index = entryIndex; index < window.Count; index++)
        {
            var session = window[index];
            var high = session.HighPrice ?? session.ClosePrice;
            var low = session.LowPrice ?? session.ClosePrice;

            if (low <= item.StopLoss)
            {
                ApplyResolution(
                    item,
                    SignalOutcomeStatus.LOSS_STOP,
                    session.TradeDate,
                    item.StopLoss,
                    NetRealizedR(item.EntryPrice, item.StopLoss, netRiskPerShare),
                    index + 1);
                return true;
            }

            // The fill bar cannot also bank the target: the entry only exists because price
            // traded down to it, so a high above the target in that same session is not this
            // trade's profit.
            if (index > entryIndex && high >= item.TargetPrice)
            {
                ApplyResolution(
                    item,
                    SignalOutcomeStatus.WIN_TARGET,
                    session.TradeDate,
                    item.TargetPrice,
                    NetRealizedR(item.EntryPrice, item.TargetPrice, netRiskPerShare),
                    index + 1);
                return true;
            }
        }

        // The horizon has not elapsed yet: the pick stays OPEN and is graded again after
        // the next session arrives.
        if (window.Count < forwardWindowBars)
        {
            return false;
        }

        var last = window[^1];
        var expiryR = NetRealizedR(item.EntryPrice, last.ClosePrice, netRiskPerShare);

        ApplyResolution(
            item,
            expiryR > 0m ? SignalOutcomeStatus.EXPIRED_WIN : SignalOutcomeStatus.EXPIRED_LOSS,
            last.TradeDate,
            last.ClosePrice,
            expiryR,
            window.Count);

        return true;
    }

    private static void ApplyResolution(
        SignalItem item,
        SignalOutcomeStatus status,
        DateOnly outcomeDate,
        decimal? exitPrice,
        decimal? realizedNetR,
        int barsHeld)
    {
        item.OutcomeStatus = status;
        item.OutcomeDate = outcomeDate;
        item.OutcomeExitPrice = exitPrice;
        item.RealizedNetR = realizedNetR;
        item.BarsHeld = barsHeld;
    }

    /// <summary>
    /// Realized result in R: the fee-adjusted per-share move divided by the fee-adjusted
    /// risk per share. A clean stop is about -1.0R and a clean target is the net R:R. Each leg is
    /// charged its own rate — commission and VAT on the buy, plus the 0.10% stock transaction tax
    /// on the sell — because the sell leg is roughly four times the buy leg.
    /// </summary>
    private static decimal NetRealizedR(decimal entry, decimal exit, decimal netRiskPerShare)
    {
        if (entry <= 0m || exit <= 0m)
        {
            return 0m;
        }

        var netPerShare = (exit - entry) -
                          (entry * PseBoardLotService.BuyLegFeeRate) -
                          (exit * PseBoardLotService.SellLegFeeRate);

        return decimal.Round(netPerShare / netRiskPerShare, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Aggregates the ledger into a scorecard for one horizon: realized win rate,
    /// expectancy, profit factor, Brier calibration and the TRADE / SMALL_SIZE /
    /// WATCH_ONLY breakdown. Picks that are still OPEN are counted but excluded from
    /// the statistics, so an early scorecard never pretends to be a track record. Picks without a
    /// stored forecast (rows persisted before the odds migration) count toward realized performance
    /// but are excluded from the Brier score and the calibration buckets.
    /// </summary>
    public async Task<SignalScorecardDto> BuildScorecardAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default)
    {
        var runs = await _repository.GetAllRunsAsync(horizon, cancellationToken);
        var entries = runs
            .SelectMany(run => run.Items.Select(item => (Run: run, Item: item)))
            .ToList();

        var resolved = entries
            .Where(entry => entry.Item.OutcomeStatus != SignalOutcomeStatus.OPEN &&
                            entry.Item.RealizedNetR is decimal)
            .ToList();

        // Only genuinely live picks are "still open". An UNFILLED pick is neither resolved nor
        // open: no position was ever held, so it carries no realized R and must not be counted as
        // an open trade or as a loss.
        var openCount = entries.Count(entry => entry.Item.OutcomeStatus == SignalOutcomeStatus.OPEN);
        var realizedR = resolved.Select(entry => entry.Item.RealizedNetR!.Value).ToList();
        var wins = resolved
            .Where(entry => entry.Item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN)
            .ToList();

        var winRate = resolved.Count == 0
            ? 0m
            : decimal.Round((decimal)wins.Count / resolved.Count, 4, MidpointRounding.AwayFromZero);

        var averageR = realizedR.Count == 0
            ? 0m
            : decimal.Round(realizedR.Average(), 4, MidpointRounding.AwayFromZero);

        var grossWin = realizedR.Where(r => r > 0m).Sum();
        var grossLoss = Math.Abs(realizedR.Where(r => r < 0m).Sum());
        var profitFactor = resolved.Count == 0
            ? 0m
            : grossLoss == 0m
                ? (grossWin > 0m ? 99m : 0m)
                : decimal.Round(grossWin / grossLoss, 2, MidpointRounding.AwayFromZero);

        // Rows persisted before the odds migration carry no forecast at all, so grading their
        // `WinProbability` (0) against the realized result would punish the desk for a number it
        // never produced. They stay in the realized performance stats — their R is real — but they
        // are excluded from the Brier score and the calibration buckets.
        var forecasted = resolved
            .Where(entry => entry.Item.WinProbability > 0m)
            .ToList();

        var brierSum = 0m;
        foreach (var entry in forecasted)
        {
            var outcome = entry.Item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN
                ? 1m
                : 0m;
            var difference = entry.Item.WinProbability - outcome;
            brierSum += difference * difference;
        }

        var brier = forecasted.Count == 0
            ? 0m
            : decimal.Round(brierSum / forecasted.Count, 4, MidpointRounding.AwayFromZero);

        var calibration = new List<CalibrationBucketDto>(5);
        for (var bucket = 0; bucket < 5; bucket++)
        {
            var low = bucket * 0.20m;
            var high = low + 0.20m;
            var members = forecasted
                .Where(entry =>
                {
                    var probability = entry.Item.WinProbability;
                    return probability >= low && (bucket == 4 ? probability <= high : probability < high);
                })
                .ToList();

            if (members.Count == 0)
            {
                continue;
            }

            var predicted = members.Average(entry => entry.Item.WinProbability);
            var actualWins = members.Count(entry =>
                entry.Item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN);

            calibration.Add(new CalibrationBucketDto(
                $"{bucket * 20}-{(bucket + 1) * 20}%",
                decimal.Round(predicted * 100m, 1, MidpointRounding.AwayFromZero),
                decimal.Round((decimal)actualWins / members.Count * 100m, 1, MidpointRounding.AwayFromZero),
                members.Count));
        }



        var verdicts = new List<VerdictBreakdownDto>();
        foreach (var verdict in Enum.GetValues<EdgeVerdict>())
        {
            var group = entries.Where(entry => entry.Item.EdgeVerdict == verdict).ToList();
            var groupResolved = group.Where(entry => entry.Item.RealizedNetR is decimal).ToList();
            var groupWins = groupResolved.Count(entry =>
                entry.Item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN);
            var groupR = groupResolved.Select(entry => entry.Item.RealizedNetR!.Value).ToList();

            verdicts.Add(new VerdictBreakdownDto(
                verdict.ToString(),
                group.Count,
                groupResolved.Count,
                groupResolved.Count == 0
                    ? 0m
                    : decimal.Round((decimal)groupWins / groupResolved.Count, 4, MidpointRounding.AwayFromZero),
                groupR.Count == 0
                    ? 0m
                    : decimal.Round(groupR.Average(), 4, MidpointRounding.AwayFromZero),
                decimal.Round(groupR.Sum(), 4, MidpointRounding.AwayFromZero)));
        }

        var recentResolved = resolved
            .OrderByDescending(entry => entry.Item.OutcomeDate ?? entry.Run.RunDate)
            .ThenByDescending(entry => entry.Run.RunDate)
            .ThenBy(entry => entry.Item.Ticker, StringComparer.Ordinal)
            .Take(25)
            .Select(entry => new ResolvedSignalDto(
                entry.Item.Ticker,
                entry.Run.RunDate,
                entry.Run.Horizon.ToString(),
                entry.Item.EdgeVerdict.ToString(),
                entry.Item.WinProbability,
                entry.Item.ExpectedValueR,
                entry.Item.OutcomeStatus.ToString(),
                entry.Item.OutcomeDate,
                entry.Item.EntryPrice,
                entry.Item.StopLoss,
                entry.Item.TargetPrice,
                entry.Item.OutcomeExitPrice,
                entry.Item.RealizedNetR,
                entry.Item.BarsHeld))
            .ToList();

        return new SignalScorecardDto(
            horizon.ToString(),
            entries.Count,
            resolved.Count,
            openCount,
            winRate,
            averageR,
            averageR,
            profitFactor,
            brier,
            calibration,
            verdicts,
            recentResolved);
    }

    /// <summary>Minimum graded forecasts before the ledger may nudge the live odds.</summary>
    public const int MinimumCalibrationSamples = 20;

    /// <summary>Pseudo-observations that shrink a small calibration sample toward zero.</summary>
    public const int CalibrationPseudoSamples = 20;

    /// <summary>Hard cap on the live calibration offset, in probability points.</summary>
    public const decimal MaximumCalibrationOffsetPct = 10m;

    /// <summary>Resolved trades a sector needs before it counts as a real pattern.</summary>
    public const int MinimumSectorSamples = 2;

    /// <summary>
    /// The desk's own forecast error in probability points: how much better or worse its
    /// graded picks resolved than the probability they were priced at. Returns 0 until
    /// <see cref="MinimumCalibrationSamples"/> forecasts exist, shrinks the raw gap by the
    /// sample size and clamps it, so a short lucky streak cannot rewrite the odds.
    /// </summary>
    public async Task<decimal> ComputeLiveCalibrationOffsetPctAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default)
    {
        var runs = await _repository.GetAllRunsAsync(horizon, cancellationToken);

        var graded = runs
            .SelectMany(run => run.Items)
            .Where(item => item.OutcomeStatus != SignalOutcomeStatus.OPEN &&
                           item.RealizedNetR is decimal &&
                           item.WinProbability > 0m)
            .ToList();

        if (graded.Count < MinimumCalibrationSamples)
        {
            return 0m;
        }

        var wins = graded.Count(item =>
            item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN);
        var actual = (decimal)wins / graded.Count;
        var predicted = graded.Average(item => item.WinProbability);
        var weight = (decimal)graded.Count / (graded.Count + CalibrationPseudoSamples);

        return decimal.Round(
            Math.Clamp(
                (actual - predicted) * 100m * weight,
                -MaximumCalibrationOffsetPct,
                MaximumCalibrationOffsetPct),
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The desk's memory of its own resolved ledger, ready for the prompt and the terminal:
    /// realized win rate, expectancy, calibration bias, the sectors that actually paid versus
    /// the ones that did not, the factors the promoted model leans on and the most recent
    /// stop-outs. Every number is computed from stored picks, never from model claims.
    /// </summary>
    public async Task<DeskMemorySummary> BuildDeskMemoryAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken = default)
    {
        var runs = await _repository.GetAllRunsAsync(horizon, cancellationToken);
        var entries = runs
            .SelectMany(run => run.Items.Select(item => (Run: run, Item: item)))
            .ToList();

        if (entries.Count == 0)
        {
            return DeskMemorySummary.Empty(horizon.ToString());
        }

        var resolved = entries
            .Where(entry => entry.Item.OutcomeStatus != SignalOutcomeStatus.OPEN &&
                            entry.Item.RealizedNetR is decimal)
            .ToList();
        var realizedR = resolved.Select(entry => entry.Item.RealizedNetR!.Value).ToList();
        var wins = resolved.Count(entry =>
            entry.Item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN);

        var winRate = resolved.Count == 0
            ? 0m
            : decimal.Round((decimal)wins / resolved.Count, 4, MidpointRounding.AwayFromZero);
        var expectancy = realizedR.Count == 0
            ? 0m
            : decimal.Round(realizedR.Average(), 4, MidpointRounding.AwayFromZero);

        var forecasted = resolved.Where(entry => entry.Item.WinProbability > 0m).ToList();
        var brierSum = forecasted.Sum(entry =>
        {
            var outcome = entry.Item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN
                ? 1m
                : 0m;
            var difference = entry.Item.WinProbability - outcome;

            return difference * difference;
        });
        var brier = forecasted.Count == 0
            ? 0m
            : decimal.Round(brierSum / forecasted.Count, 4, MidpointRounding.AwayFromZero);
        var bias = forecasted.Count == 0
            ? 0m
            : decimal.Round(
                (winRate - forecasted.Average(entry => entry.Item.WinProbability)) * 100m,
                2,
                MidpointRounding.AwayFromZero);

        var sectors = await LoadSectorMemoryAsync(resolved, cancellationToken);
        var model = await _repository.GetTrainedModelAsync(horizon, cancellationToken);

        var stopOuts = resolved
            .Where(entry => entry.Item.OutcomeStatus == SignalOutcomeStatus.LOSS_STOP)
            .OrderByDescending(entry => entry.Item.OutcomeDate ?? entry.Run.RunDate)
            .Take(5)
            .Select(entry => new LossPostMortemDto(
                entry.Item.Ticker,
                entry.Run.RunDate,
                entry.Item.EntryPrice,
                entry.Item.StopLoss,
                entry.Item.RealizedNetR!.Value,
                entry.Item.BarsHeld ?? 0))
            .ToList();

        return new DeskMemorySummary(
            horizon.ToString(),
            resolved.Count,
            winRate,
            expectancy,
            brier,
            bias,
            sectors.OrderByDescending(sector => sector.AverageRealizedNetR).Take(3).ToList(),
            sectors.OrderBy(sector => sector.AverageRealizedNetR).Take(3).ToList(),
            TrainedModelPresenter.ToWeightDtos(model).Take(5).ToList(),
            stopOuts);
    }

    private async Task<IReadOnlyList<SectorMemoryDto>> LoadSectorMemoryAsync(
        IReadOnlyList<(SignalRun Run, SignalItem Item)> resolved,
        CancellationToken cancellationToken)
    {
        if (resolved.Count == 0)
        {
            return Array.Empty<SectorMemoryDto>();
        }

        var metrics = await _repository.GetMetricsRangeAsync(
            resolved.Min(entry => entry.Run.RunDate),
            cancellationToken);

        var sectorByTicker = metrics
            .Where(metric => !string.IsNullOrWhiteSpace(metric.Sector))
            .GroupBy(metric => metric.Ticker, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(metric => metric.TradeDate).Last().Sector,
                StringComparer.OrdinalIgnoreCase);

        return resolved
            .Where(entry => sectorByTicker.ContainsKey(entry.Item.Ticker))
            .GroupBy(entry => sectorByTicker[entry.Item.Ticker], StringComparer.OrdinalIgnoreCase)
            .Select(group => new SectorMemoryDto(
                group.Key,
                group.Count(),
                decimal.Round(
                    (decimal)group.Count(entry =>
                        entry.Item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN) /
                    group.Count(),
                    4,
                    MidpointRounding.AwayFromZero),
                decimal.Round(group.Average(entry => entry.Item.RealizedNetR!.Value), 4, MidpointRounding.AwayFromZero)))
            .Where(sector => sector.Samples >= MinimumSectorSamples)
            .ToList();
    }
}

