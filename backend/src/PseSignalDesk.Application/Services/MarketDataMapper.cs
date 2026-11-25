using System.Text.Json;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Maps between the application models, persisted entities and API DTOs.
/// </summary>
public static class MarketDataMapper
{
    private static readonly JsonSerializerOptions DisclosureJsonOptions = new(JsonSerializerDefaults.Web);

    public static DailyMarketSnapshot ToSnapshot(MarketDataBatch batch) => new()
    {
        TradeDate = batch.TradeDate,
        PseiClose = batch.PseiClose,
        PseiSma50 = batch.PseiSma50,
        PseiSma200 = batch.PseiSma200,
        NetForeignFlowPhp = batch.NetForeignFlowPhp,
        UsdPhp = batch.UsdPhp,
        MarketRegime = batch.MarketRegime,
        RegimeNote = batch.RegimeNote,
        BreadthAboveSma50Pct = batch.Breadth?.AboveSma50Pct,
        BreadthAboveSma200Pct = batch.Breadth?.AboveSma200Pct,
        Stage2Count = batch.Breadth?.Stage2Count,
        EvaluatedStockCount = batch.Breadth?.EvaluatedStockCount,
        UsdPhpSma50 = batch.UsdPhpSma50,
        UsdPhpChange20dPct = batch.UsdPhpChange20dPct,
        PseiRealizedVol20dPct = batch.PseiRealizedVol20dPct,
        SectorRotationJson = SerializeSectorRotation(batch.SectorRotation),
        Us10YClosePct = batch.Us10YClosePct,
        Us10YChange20dBps = batch.Us10YChange20dBps,
        BrentUsd = batch.BrentUsd,
        BrentChange20dPct = batch.BrentChange20dPct,
        EpheCloseUsd = batch.EpheCloseUsd,
        EpheChange20dPct = batch.EpheChange20dPct,
        ForeignFlowSource = batch.ForeignFlowSource
    };

    /// <summary>
    /// Rebuilds a batch from a stored snapshot so a later write (for example an uploaded
    /// Daily Quotation Report) cannot silently drop macro dials, breadth or sector rotation.
    /// </summary>
    public static MarketDataBatch ToBatch(DailyMarketSnapshot snapshot)
    {
        var breadth = snapshot.BreadthAboveSma50Pct is null
            && snapshot.BreadthAboveSma200Pct is null
            && snapshot.Stage2Count is null
            && snapshot.EvaluatedStockCount is null
            ? null
            : new MarketBreadth(
                snapshot.BreadthAboveSma50Pct,
                snapshot.BreadthAboveSma200Pct,
                snapshot.Stage2Count ?? 0,
                snapshot.EvaluatedStockCount ?? 0);

        return new MarketDataBatch(
            snapshot.TradeDate,
            snapshot.PseiClose,
            snapshot.PseiSma50,
            snapshot.PseiSma200,
            snapshot.NetForeignFlowPhp,
            snapshot.UsdPhp,
            snapshot.MarketRegime,
            snapshot.RegimeNote,
            Stocks: Array.Empty<StockMarketData>(),
            Source: "snapshot",
            Breadth: breadth,
            UsdPhpSma50: snapshot.UsdPhpSma50,
            UsdPhpChange20dPct: snapshot.UsdPhpChange20dPct,
            PseiRealizedVol20dPct: snapshot.PseiRealizedVol20dPct,
            SectorRotation: DeserializeSectorRotation(snapshot.SectorRotationJson),
            Us10YClosePct: snapshot.Us10YClosePct,
            Us10YChange20dBps: snapshot.Us10YChange20dBps,
            BrentUsd: snapshot.BrentUsd,
            BrentChange20dPct: snapshot.BrentChange20dPct,
            EpheCloseUsd: snapshot.EpheCloseUsd,
            EpheChange20dPct: snapshot.EpheChange20dPct,
            ForeignFlowSource: snapshot.ForeignFlowSource);
    }

    public static string SerializeSectorRotation(IReadOnlyList<SectorRotationDto>? rotation)
    {
        return rotation is null || rotation.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(rotation, DisclosureJsonOptions);
    }

    public static IReadOnlyList<SectorRotationDto> DeserializeSectorRotation(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SectorRotationDto>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<SectorRotationDto>>(json, DisclosureJsonOptions)
                   ?? new List<SectorRotationDto>();
        }
        catch (JsonException)
        {
            return Array.Empty<SectorRotationDto>();
        }
    }

    public static string SerializeDisclosures(IReadOnlyList<StockDisclosure>? disclosures)
    {
        return disclosures is null || disclosures.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(disclosures, DisclosureJsonOptions);
    }

    public static IReadOnlyList<StockDisclosure> DeserializeDisclosures(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<StockDisclosure>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<StockDisclosure>>(json, DisclosureJsonOptions)
                   ?? new List<StockDisclosure>();
        }
        catch (JsonException)
        {
            return Array.Empty<StockDisclosure>();
        }
    }

    public static SignalCandidateInput ToCandidate(StockDailyMetric metric, string? sectorQuadrant = null)
    {
        var disclosures = DeserializeDisclosures(metric.RecentDisclosuresJson);
        var limits = metric.ClosePrice > 0m
            ? PseBoardLotService.GetStaticPriceLimits(metric.ClosePrice)
            : null;

        return new SignalCandidateInput(
            metric.Ticker,
            metric.CompanyName,
            metric.Sector,
            metric.ClosePrice,
            metric.ChangePct,
            metric.AvgTurnover20dPhp,
            metric.VolumeRatioVs20d,
            metric.Rsi14,
            metric.Ema20,
            metric.Sma50,
            metric.Sma200,
            metric.Atr14,
            metric.SupportLevel,
            metric.ResistanceLevel,
            metric.NetForeignTodayPhp,
            metric.QuantPreScore,
            disclosures,
            BoardLot: PseBoardLotService.GetBoardLotSize(metric.ClosePrice),
            StaticCeiling: limits?.StaticCeiling ?? 0m,
            StaticFloor: limits?.StaticFloor ?? 0m,
            TrapFlags: PseDisclosureClassifier.DetectTrapFlags(disclosures),
            Sma150: metric.Sma150,
            Week52High: metric.Week52High,
            WeinsteinStage: metric.WeinsteinStage,
            RelativeStrength63d: metric.RelativeStrength63d,
            AtrPct: metric.AtrPct,
            VolatilityContractionRatio: metric.VolatilityContractionRatio,
            EmpiricalWinRate: metric.EmpiricalWinRate,
            TrailingDividendPerShare: metric.TrailingDividendPerShare,
            TrailingDividendYieldPct: metric.TrailingDividendYieldPct,
            LastExDividendDate: metric.LastExDividendDate,
            EmpiricalWinRateDay: metric.EmpiricalWinRateDay,
            EmpiricalWinRateMonth: metric.EmpiricalWinRateMonth,
            EmpiricalWinRateYear: metric.EmpiricalWinRateYear,
            UpDownVolumeRatio20d: metric.UpDownVolumeRatio20d,
            ChaikinMoneyFlow20d: metric.ChaikinMoneyFlow20d,
            TrendEfficiency63d: metric.TrendEfficiency63d,
            Beta252d: metric.Beta252d,
            MaxDrawdown252dPct: metric.MaxDrawdown252dPct,
            SectorQuadrant: sectorQuadrant,
            TickSpreadPct: metric.ClosePrice > 0m
                ? decimal.Round(
                    PseBoardLotService.ComputeTickSpreadRate(metric.ClosePrice) * 100m,
                    4,
                    MidpointRounding.AwayFromZero)
                : null);
    }

    public static TechnicalMetricsDto ToMetricsDto(StockDailyMetric metric) => new(
        metric.ClosePrice,
        metric.ChangePct,
        metric.AvgTurnover20dPhp,
        metric.VolumeRatioVs20d,
        metric.Rsi14,
        metric.Ema20,
        metric.Sma50,
        metric.Sma200,
        metric.Atr14,
        metric.SupportLevel,
        metric.ResistanceLevel,
        metric.NetForeignTodayPhp,
        metric.QuantPreScore,
        metric.Sma150,
        metric.Week52High,
        metric.WeinsteinStage,
        metric.OpenPrice,
        metric.HighPrice,
        metric.LowPrice,
        metric.RelativeStrength63d,
        metric.AtrPct,
        metric.VolatilityContractionRatio,
        metric.EmpiricalWinRate,
        metric.TrailingDividendPerShare,
        metric.TrailingDividendYieldPct,
        metric.LastExDividendDate,
        metric.EmpiricalWinRateDay,
        metric.EmpiricalWinRateMonth,
        metric.EmpiricalWinRateYear,
        metric.UpDownVolumeRatio20d,
        metric.ChaikinMoneyFlow20d,
        metric.TrendEfficiency63d,
        metric.Beta252d,
        metric.MaxDrawdown252dPct,
        // Derived, not stored: the tick spread is a property of the price bracket, so it is
        // recomputed from the official table instead of being persisted per session.
        metric.ClosePrice > 0m
            ? decimal.Round(PseBoardLotService.ComputeTickSpreadRate(metric.ClosePrice) * 100m, 4, MidpointRounding.AwayFromZero)
            : null);

    public static IReadOnlyList<StockDisclosureDto> ToDisclosureDtos(string? json)
    {
        return DeserializeDisclosures(json)
            .OrderByDescending(d => d.Date)
            .Select(d => new StockDisclosureDto(
                d.Date,
                d.Headline,
                d.Category,
                d.Source,
                PseDisclosureClassifier.Classify(d.Headline).TrapFlag.ToString()))
            .ToList();
    }

    public static MarketRegimeDto ToRegimeDto(
        DailyMarketSnapshot snapshot,
        SignalRun? run = null) => new(
        snapshot.TradeDate,
        snapshot.PseiClose,
        snapshot.PseiSma50,
        snapshot.PseiSma200,
        AboveBelow(snapshot.PseiClose, snapshot.PseiSma50),
        AboveBelow(snapshot.PseiClose, snapshot.PseiSma200),
        snapshot.NetForeignFlowPhp,
        snapshot.UsdPhp,
        snapshot.MarketRegime.ToString(),
        snapshot.RegimeNote,
        snapshot.BreadthAboveSma50Pct,
        snapshot.BreadthAboveSma200Pct,
        snapshot.Stage2Count,
        snapshot.EvaluatedStockCount,
        snapshot.UsdPhpSma50,
        snapshot.UsdPhpChange20dPct,
        snapshot.PseiRealizedVol20dPct,
        DeserializeSectorRotation(snapshot.SectorRotationJson),
        snapshot.Us10YClosePct,
        snapshot.Us10YChange20dBps,
        snapshot.BrentUsd,
        snapshot.BrentChange20dPct,
        snapshot.EpheCloseUsd,
        snapshot.EpheChange20dPct,
        snapshot.ForeignFlowSource,
        // The traffic light is derived from this same session, so it can never disagree
        // with the dials the strip prints beside it.
        MarketDirectionService.Evaluate(snapshot, run));

    public static string AboveBelow(decimal close, decimal reference)
    {
        if (reference <= 0m)
        {
            return "N/A";
        }

        return close >= reference ? "ABOVE" : "BELOW";
    }
}
