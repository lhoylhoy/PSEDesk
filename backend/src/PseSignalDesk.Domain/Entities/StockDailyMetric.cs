using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// Fully computed technical picture for a single PSE ticker on a single trading day.
/// Persisted so historical runs and the stock detail drawer stay reproducible even
/// when the upstream data feed has moved on.
/// </summary>
public class StockDailyMetric
{
    public int Id { get; set; }

    public DateOnly TradeDate { get; set; }

    public string Ticker { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string Sector { get; set; } = string.Empty;

    public decimal ClosePrice { get; set; }

    public decimal ChangePct { get; set; }

    public decimal AvgTurnover20dPhp { get; set; }

    public decimal VolumeRatioVs20d { get; set; }

    public decimal Rsi14 { get; set; }

    public decimal Ema20 { get; set; }

    public decimal Sma50 { get; set; }

    public decimal Sma200 { get; set; }

    public decimal Atr14 { get; set; }

    public decimal SupportLevel { get; set; }

    public decimal ResistanceLevel { get; set; }

    /// <summary>
    /// Net foreign buying/selling for the ticker on this session. Null means
    /// "unreported": the official figure only arrives when an official PSE Daily Quotation
    /// Report was parsed for the session (live template or operator upload), and an estimate
    /// must never be stored as if it were a real observation.
    /// </summary>
    public decimal? NetForeignTodayPhp { get; set; }

    /// <summary>
    /// JSON array of recent PSE Edge disclosures so the LLM can audit catalysts
    /// without a live call during post-processing or replay.
    /// </summary>
    public string RecentDisclosuresJson { get; set; } = "[]";

    /// <summary>
    /// Deterministic 0-100 pre-score produced by the quantitative filter. The LLM
    /// may re-rank, but this anchors the candidate list and its ordering.
    /// </summary>
    public int QuantPreScore { get; set; }

    /// <summary>
    /// 150-day (30-week) simple moving average: Weinstein's Stage Analysis baseline.
    /// Null when the series is shorter than 150 sessions.
    /// </summary>
    public decimal? Sma150 { get; set; }

    /// <summary>
    /// Highest close of the trailing 52 weeks (up to 252 sessions). Null when the series
    /// is shorter than 200 sessions, which is already the candidate floor.
    /// </summary>
    public decimal? Week52High { get; set; }

    /// <summary>
    /// Weinstein stage derived from the 150D average and its 20-session slope:
    /// STAGE_1 (basing), STAGE_2 (advancing), STAGE_3 (distribution), STAGE_4 (declining).
    /// Null when there is not enough history to slope the average.
    /// </summary>
    public string? WeinsteinStage { get; set; }

    /// <summary>
    /// Session open, tick-rounded. Null for rows persisted before intraday OHLC was
    /// captured; the forward-outcome resolver then falls back to the close.
    /// </summary>
    public decimal? OpenPrice { get; set; }

    /// <summary>
    /// Session high, tick-rounded. Drives real target-touch grading of prior picks.
    /// </summary>
    public decimal? HighPrice { get; set; }

    /// <summary>
    /// Session low, tick-rounded. Drives real stop-touch grading of prior picks.
    /// </summary>
    public decimal? LowPrice { get; set; }

    /// <summary>
    /// 63-session return difference versus the PSEi, in percentage points. Positive means
    /// the name is outperforming the index. Null when history is too short.
    /// </summary>
    public decimal? RelativeStrength63d { get; set; }

    /// <summary>
    /// ATR14 as a percentage of the close: the volatility budget a stop has to survive.
    /// </summary>
    public decimal? AtrPct { get; set; }

    /// <summary>
    /// Short-term ATR(5) divided by ATR(14). A ratio below 0.85 is a volatility
    /// contraction (VCP-style) base; higher means the range is expanding.
    /// </summary>
    public decimal? VolatilityContractionRatio { get; set; }

    /// <summary>
    /// Shrunk historical barrier hit-rate for this ticker's trend-aligned WEEK setups
    /// (a 10-bar, 1.5x ATR stop / 3.0x ATR target bet). Null when the series is too short to
    /// estimate a base rate. The per-horizon columns below carry the other three books'
    /// geometries; <see cref="EmpiricalWinRateFor"/> picks the right one.
    /// </summary>
    public decimal? EmpiricalWinRate { get; set; }

    /// <summary>
    /// Empirical barrier hit rate shaped by the DAY profile (1.2x stop / 2.6x target / 3-bar
    /// window). Null when the series is too short.
    /// </summary>
    public decimal? EmpiricalWinRateDay { get; set; }

    /// <summary>Empirical barrier hit rate shaped by the MONTH profile (2.0x / 4.0x / 42-bar window).</summary>
    public decimal? EmpiricalWinRateMonth { get; set; }

    /// <summary>Empirical barrier hit rate shaped by the YEAR profile (2.5x / 5.0x / 126-bar window).</summary>
    public decimal? EmpiricalWinRateYear { get; set; }

    /// <summary>
    /// 20-session up-volume divided by down-volume. Above 1.25 means the accumulated volume
    /// is happening on advancing sessions — the smart-money accumulation footprint the desk
    /// can build because PSE net foreign flow is unpublished. Null when there is no
    /// down-volume (or too little history) to divide by.
    /// </summary>
    public decimal? UpDownVolumeRatio20d { get; set; }

    /// <summary>
    /// Chaikin Money Flow over 20 sessions: volume-weighted closing location in
    /// <c>[-1, 1]</c>. Positive values mean closes are happening near session highs (quiet
    /// accumulation); negative values mean intraday fades (distribution). Null when a
    /// session had a zero range or no volume.
    /// </summary>
    public decimal? ChaikinMoneyFlow20d { get; set; }

    /// <summary>
    /// Kaufman 63-session efficiency ratio: net move divided by the total path travelled, in
    /// <c>[0, 1]</c>. High values are smooth institutional trends; low values are chop that
    /// pays the round trip without going anywhere.
    /// </summary>
    public decimal? TrendEfficiency63d { get; set; }

    /// <summary>
    /// Empirical beta of the ticker's daily returns against the PSEi over up to 252
    /// sessions. Null when the index series is unavailable or fewer than 60 overlapping
    /// returns exist.
    /// </summary>
    public decimal? Beta252d { get; set; }

    /// <summary>
    /// Worst peak-to-trough close drawdown over the trailing 252 sessions, in percent
    /// (positive number). The YEAR sleeve prefers names under ~22%.
    /// </summary>
    public decimal? MaxDrawdown252dPct { get; set; }

    /// <summary>
    /// Cash dividends per share paid over the trailing 365 calendar days, summed from the
    /// Yahoo Finance chart dividend events. Null when the feed published no dividend in the
    /// window (an income name out of cycle, or a non-payer) — never zero-filled, because
    /// "no dividend in the window" and "pays nothing" are different claims.
    /// </summary>
    public decimal? TrailingDividendPerShare { get; set; }

    /// <summary>
    /// Trailing twelve-month cash yield as a percentage of the close. Null whenever
    /// <see cref="TrailingDividendPerShare"/> is null.
    /// </summary>
    public decimal? TrailingDividendYieldPct { get; set; }

    /// <summary>
    /// Most recent ex-dividend date in the trailing window. Used to avoid misreading the
    /// mechanical ex-date markdown as a technical breakdown.
    /// </summary>
    public DateOnly? LastExDividendDate { get; set; }

    /// <summary>
    /// The empirical barrier hit rate that matches the book being priced: DAY/MONTH/YEAR use
    /// their own column and fall back to the WEEK figure for rows persisted before the
    /// per-horizon columns existed. Pricing a DAY plan off a WEEK base rate is the bug this
    /// accessor exists to close.
    /// </summary>
    public decimal? EmpiricalWinRateFor(SignalHorizon horizon) => horizon switch
    {
        SignalHorizon.DAY => EmpiricalWinRateDay ?? EmpiricalWinRate,
        SignalHorizon.MONTH => EmpiricalWinRateMonth ?? EmpiricalWinRate,
        SignalHorizon.YEAR => EmpiricalWinRateYear ?? EmpiricalWinRate,
        _ => EmpiricalWinRate
    };

    /// <summary>
    /// Returns a read-only copy with a narrower disclosure list, so a run can show one horizon
    /// the filings inside its own window while the persisted row keeps the full rolling history.
    /// </summary>
    public StockDailyMetric WithDisclosures(string disclosuresJson)
    {
        var copy = (StockDailyMetric)MemberwiseClone();
        copy.RecentDisclosuresJson = disclosuresJson;
        return copy;
    }
}
