using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Models;

/// <summary>
/// Everything needed to persist a trading day and compute signals for it.
/// </summary>
public sealed record MarketDataBatch(
    DateOnly TradeDate,
    decimal PseiClose,
    decimal PseiSma50,
    decimal PseiSma200,
    decimal? NetForeignFlowPhp,
    decimal UsdPhp,
    MarketRegime MarketRegime,
    string RegimeNote,
    IReadOnlyList<StockMarketData> Stocks,
    string Source,
    IReadOnlyList<OhlcvBar>? IndexBars = null,
    MarketBreadth? Breadth = null,
    // Macro dials derived from the series the provider already fetched. Nullable because a
    // stored session written before the dials existed must read "unreported", never 0.
    decimal? UsdPhpSma50 = null,
    decimal? UsdPhpChange20dPct = null,
    decimal? PseiRealizedVol20dPct = null,
    IReadOnlyList<SectorRotationDto>? SectorRotation = null,
    // Cross-asset macro dials and foreign-flow provenance. Every field is nullable because the
    // macro feeds are best-effort: an unreported dial stays null instead of blocking a live run
    // or being stored as zero.
    decimal? Us10YClosePct = null,
    decimal? Us10YChange20dBps = null,
    decimal? BrentUsd = null,
    decimal? BrentChange20dPct = null,
    decimal? EpheCloseUsd = null,
    decimal? EpheChange20dPct = null,
    string? ForeignFlowSource = null)
{
    public static MarketDataBatch Empty(DateOnly tradeDate) => new(
        tradeDate,
        PseiClose: 0m,
        PseiSma50: 0m,
        PseiSma200: 0m,
        NetForeignFlowPhp: null,
        UsdPhp: 0m,
        MarketRegime: MarketRegime.NEUTRAL,
        RegimeNote: "No market data available for this trading day.",
        Stocks: Array.Empty<StockMarketData>(),
        Source: "none");
}
