using System.Text.Json;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Builds the API view of a persisted signal run, enriching each pick with the
/// technical metrics and disclosures captured on that trading day.
/// </summary>
public static class SignalRunMapper
{
    private static readonly JsonSerializerOptions CitationJsonOptions = new(JsonSerializerDefaults.Web);

    public static SignalRunDto ToDto(
        SignalRun run,
        IReadOnlyDictionary<string, StockDailyMetric> metricsByTicker,
        BookSizingSnapshot? book = null)
    {
        var items = run.Items
            .OrderBy(i => i.Rank)
            .Select(item =>
            {
                metricsByTicker.TryGetValue(item.Ticker, out var metric);

                // The stored edge assessment is what the governor sized against, so the ticket
                // the list renders uses the same Kelly stake the run published.
                var assessment = new EdgeAssessment(
                    item.NetRiskRewardRatio,
                    item.WinProbability,
                    item.BreakEvenWinRate,
                    item.ExpectedValueR,
                    item.QuarterKellyFraction,
                    item.EdgeVerdict);

                var disclosures = metric is null
                    ? Array.Empty<StockDisclosureDto>()
                    : MarketDataMapper.ToDisclosureDtos(metric.RecentDisclosuresJson);

                var flags = disclosures
                    .Select(disclosure => disclosure.TrapFlag)
                    .Where(flag => !string.Equals(flag, "NONE", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return new SignalItemDto(
                    item.Rank,
                    item.Ticker,
                    metric?.CompanyName ?? item.Ticker,
                    metric?.Sector ?? "PSE",
                    item.DriftStatus.ToString(),
                    item.PreviousRank,
                    item.ConvictionScore,
                    item.EntryPrice,
                    item.StopLoss,
                    item.TargetPrice,
                    item.RiskRewardRatio,
                    item.Thesis,
                    item.DeviationReason,
                    metric is null ? null : MarketDataMapper.ToMetricsDto(metric),
                    disclosures,
                    PseBoardLotService.GetBoardLotSize(item.EntryPrice),
                    item.BoardLotShares,
                    ReadCitations(item.CitedReferencesJson),
                    flags,
                    item.NetRiskRewardRatio,
                    item.WinProbability,
                    item.BreakEvenWinRate,
                    item.ExpectedValueR,
                    item.QuarterKellyFraction,
                    item.EdgeVerdict.ToString(),
                    item.BearCase,
                    item.OutcomeStatus.ToString(),
                    item.OutcomeDate,
                    item.OutcomeExitPrice,
                    item.RealizedNetR,
                    item.BarsHeld,
                    // Sized against this candidate's own sector room, ATR and pyramid state, not a
                    // run-wide ticker-less context: a second name in a full sector, or a legal add
                    // to an open winner, is a different order from a fresh first position.
                    book is null
                        ? null
                        : OrderTicketCalculator.Build(
                            book.For(item.Ticker),
                            item.EntryPrice,
                            item.StopLoss,
                            item.TargetPrice,
                            item.EdgeVerdict,
                            metric?.AvgTurnover20dPhp ?? 0m,
                            assessment),
                    WaveTradePlanCalculator.BuildPlan(
                        item.EntryPrice,
                        item.StopLoss,
                        item.TargetPrice,
                        metric?.ClosePrice ?? 0m,
                        metric?.Ema20,
                        item.EdgeVerdict,
                        run.Horizon));
            })
            .ToList();

        var drops = run.Drops
            .OrderBy(d => d.PreviousRank ?? int.MaxValue)
            .ThenBy(d => d.Ticker, StringComparer.Ordinal)
            .Select(drop =>
            {
                metricsByTicker.TryGetValue(drop.Ticker, out var metric);

                return new SignalDropDto(
                    drop.Ticker,
                    metric?.CompanyName ?? drop.Ticker,
                    drop.PreviousRank,
                    drop.DropCategory.ToString(),
                    drop.Reason);
            })
            .ToList();

        return new SignalRunDto(
            run.Id,
            run.RunDate,
            run.Horizon.ToString(),
            run.CreatedAtUtc,
            items,
            drops,
            run.RecommendedCashPct,
            run.Items.Count(item => item.EdgeVerdict == EdgeVerdict.TRADE),
            run.Items.Count(item => item.EdgeVerdict == EdgeVerdict.SMALL_SIZE),
            run.Items.Count(item => item.EdgeVerdict == EdgeVerdict.WATCH_ONLY),
            run.EvaluatedStockCount is null
                ? null
                : new MarketBreadthDto(
                    run.BreadthAboveSma50Pct,
                    run.BreadthAboveSma200Pct,
                    run.Stage2Count ?? 0,
                    run.EvaluatedStockCount.Value));
    }

    public static IReadOnlyList<string> ReadCitations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, CitationJsonOptions) ?? new List<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }
}
