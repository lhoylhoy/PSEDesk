using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// One row per trading day capturing the PSEi tape, foreign flow and macro backdrop
/// the signal run was produced against.
/// </summary>
public class DailyMarketSnapshot
{
    public int Id { get; set; }

    public DateOnly TradeDate { get; set; }

    public decimal PseiClose { get; set; }

    public decimal PseiSma50 { get; set; }

    public decimal PseiSma200 { get; set; }

    /// <summary>
    /// Net foreign buying/selling for the session, from the official PSE Daily Quotation
    /// Report (live template or operator upload). Null means "unreported": the report was
    /// missing or unreadable, and an estimate must never be stored as if it were a real
    /// observation. <see cref="ForeignFlowSource"/> carries the provenance.
    /// </summary>
    public decimal? NetForeignFlowPhp { get; set; }

    public decimal UsdPhp { get; set; }

    public MarketRegime MarketRegime { get; set; }

    public string RegimeNote { get; set; } = string.Empty;

    /// <summary>
    /// Percentage of the evaluated universe trading above its own 50-day average on this
    /// session. Null when the session was stored before breadth was computed, so the UI
    /// can show "unreported" instead of a fabricated 0%.
    /// </summary>
    public decimal? BreadthAboveSma50Pct { get; set; }

    /// <summary>
    /// Percentage of the evaluated universe trading above its own 200-day average.
    /// </summary>
    public decimal? BreadthAboveSma200Pct { get; set; }

    /// <summary>
    /// Number of evaluated names in Weinstein Stage 2 (advancing) on this session.
    /// </summary>
    public int? Stage2Count { get; set; }

    /// <summary>
    /// How many tickers the breadth calculation considered, so a percentage is never
    /// mistaken for coverage of the whole exchange.
    /// </summary>
    public int? EvaluatedStockCount { get; set; }

    /// <summary>
    /// 50-day average of the USD/PHP close. A peso trading above this average is weakening:
    /// imported inflation and EM foreign outflows are headwinds for the index. Null on
    /// snapshots stored before the FX series was kept.
    /// </summary>
    public decimal? UsdPhpSma50 { get; set; }

    /// <summary>
    /// 20-session change in USD/PHP in percent. Positive means the peso depreciated (a
    /// headwind for importers and for foreign appetite); null when the FX history is short.
    /// </summary>
    public decimal? UsdPhpChange20dPct { get; set; }

    /// <summary>
    /// PSEi close-to-close realized volatility over the last 20 sessions, annualised
    /// (<c>stdev * sqrt(252) * 100</c>). The volatility-targeting scalar divides the desk's
    /// target volatility by this figure. Null when fewer than 21 index bars exist.
    /// </summary>
    public decimal? PseiRealizedVol20dPct { get; set; }

    /// <summary>
    /// JSON array of <c>SectorRotationDto</c> reads for the session. Persisted so a stored
    /// day keeps the rotation it was ranked against; recomputed from the session's own
    /// metrics when the column is null (rows written before the dial existed).
    /// </summary>
    public string? SectorRotationJson { get; set; }

    /// <summary>
    /// US 10-year Treasury yield in percent, read from the zero-auth Yahoo
    /// <c>^TNX</c> chart. This is the global risk-free rate the desk compares a PSE
    /// dividend yield against. Null means the macro feed was unavailable or the session
    /// predates the dial — never zero.
    /// </summary>
    public decimal? Us10YClosePct { get; set; }

    /// <summary>
    /// 20-session change in the US 10-year yield, in basis points (100 bps = 1 percentage
    /// point). Positive means global rates rose, which pressures bond-proxy equities.
    /// </summary>
    public decimal? Us10YChange20dBps { get; set; }

    /// <summary>
    /// Brent crude close in USD per barrel from <c>BZ=F</c>. Imported energy inflation is a
    /// direct margin input for Philippine transport, power and consumer names.
    /// </summary>
    public decimal? BrentUsd { get; set; }

    /// <summary>
    /// 20-session percentage change in Brent crude. Null when the macro series is short.
    /// </summary>
    public decimal? BrentChange20dPct { get; set; }

    /// <summary>
    /// iShares MSCI Philippines ETF (<c>EPHE</c>) close in USD: the offshore institutional
    /// read on Philippine equities. Deliberately stored apart from
    /// <see cref="NetForeignFlowPhp"/>, which is the official PSE figure — the two are never
    /// conflated.
    /// </summary>
    public decimal? EpheCloseUsd { get; set; }

    /// <summary>
    /// 20-session percentage return of <c>EPHE</c>. Null when unreported.
    /// </summary>
    public decimal? EpheChange20dPct { get; set; }

    /// <summary>
    /// Where <see cref="NetForeignFlowPhp"/> came from: <c>"pse-dqr-live"</c> when the
    /// official PSE Daily Quotation Report was fetched automatically, <c>"pse-dqr-upload"</c>
    /// when an operator uploaded it, or null when foreign flow is unreported. The provenance
    /// travels with the number so a stored session never implies a source it did not have.
    /// </summary>
    public string? ForeignFlowSource { get; set; }
}
