namespace PseSignalDesk.Application.Models;

/// <summary>The deterministic ATR-anchored, tick-valid trade plan for one researched name.</summary>
public sealed record ResearchPlanDto(
    decimal EntryPrice,
    decimal StopLoss,
    decimal TargetPrice,
    decimal RiskRewardRatio,
    decimal NetRiskRewardRatio,
    decimal AtrStopDistance,
    decimal AtrTargetDistance);

/// <summary>
/// On-demand single-ticker deep dive. Everything here is server-computed from the live
/// OHLCV series (or the stored snapshot) plus the PSE Edge disclosures; the optional AI
/// thesis is additive prose that never owns a level, a size or a verdict.
/// </summary>
public sealed record TickerResearchDto(
    string Ticker,
    string CompanyName,
    string Sector,
    string Horizon,
    DateOnly AsOfDate,
    bool InCatalog,
    bool PassesLiquidityFloor,
    decimal AvgTurnover20dPhp,
    TechnicalMetricsDto Metrics,
    IReadOnlyList<string> TrapFlags,
    IReadOnlyList<StockDisclosureDto> Disclosures,
    ResearchPlanDto Plan,
    decimal WinProbability,
    decimal BreakEvenWinRate,
    decimal ExpectedValueR,
    decimal QuarterKellyFraction,
    string EdgeVerdict,
    string BearCase,
    OrderTicketDto? OrderTicket,
    string DataSource,
    string? AiThesis = null,
    // Ride-the-wave additions. Both are optional so a research response produced before
    // them still deserializes; a null position check means the reader did not say they own it.
    WaveTradePlanDto? WavePlan = null,
    PositionAdviceDto? PositionAdvice = null);

/// <summary>
/// The "I already own it" check: what the desk would do with an existing position, priced
/// from the owner's own average cost with every peso fee deducted. All prices are tick-valid
/// and the trim size is a whole board lot on both legs.
/// </summary>
public sealed record PositionAdviceDto(
    string Ticker,
    decimal AverageCost,
    decimal CurrentPrice,
    int Shares,
    decimal BreakEvenPrice,
    decimal SuggestedStopPrice,
    decimal SuggestedTargetPrice,
    /// <summary>Fee-paid return from average cost to the current price, in percent.</summary>
    decimal NetReturnPct,
    /// <summary>Fee-paid peso result; null when the reader did not give a share count.</summary>
    decimal? UnrealizedNetPnlPhp,
    decimal UnrealizedR,
    /// <summary>HOLD, STOP_BREACHED, TARGET_REACHED, MOVE_STOP_TO_BREAKEVEN or TRAIL_STOP_EMA20.</summary>
    string Action,
    string Headline,
    string Explanation,
    int RecommendedTrimShares);
