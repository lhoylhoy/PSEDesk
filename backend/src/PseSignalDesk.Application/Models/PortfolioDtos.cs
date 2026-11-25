using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Models;

/// <summary>Capital configuration and cash ledger of the single desk account.</summary>
public sealed record PortfolioAccountDto(
    decimal StartingCapitalPhp,
    decimal CashBalancePhp,
    decimal MaxRiskPerTradePct,
    decimal SmallSizeRiskMultiplier,
    decimal MaxPositionPct,
    decimal MaxPortfolioHeatPct,
    DateTime UpdatedAtUtc,
    // Pod-shop guardrails. Optional so a client written before the governor still
    // deserializes; every value is server-owned and clamped on write.
    decimal? PeakEquityPhp = null,
    decimal MaxSectorExposurePct = 35m,
    int MaxPositionsPerSector = 2,
    decimal SoftDrawdownThresholdPct = 5m,
    decimal HardDrawdownThresholdPct = 10m,
    decimal TargetAnnualVolPct = 18m);

/// <summary>
/// One open blotter position marked to the latest stored session. Every peso figure is
/// after the relevant PSE fee leg; <see cref="UnrealizedNetPnlPhp"/> deducts the sell leg
/// that would be paid if the position were closed today.
/// </summary>
public sealed record OpenPositionDto(
    int Id,
    string Ticker,
    string CompanyName,
    string Sector,
    string Horizon,
    string EdgeVerdict,
    DateOnly OpenedDate,
    int Shares,
    decimal EntryPrice,
    decimal InitialStopLoss,
    decimal CurrentStopLoss,
    decimal TargetPrice,
    decimal BuyCostPhp,
    decimal InitialNetRiskPhp,
    decimal LastPrice,
    decimal? Ema20,
    decimal UnrealizedNetPnlPhp,
    decimal UnrealizedNetR,
    decimal BreakEvenStopPrice,
    string Action,
    string ActionNote,
    int SessionsHeld,
    // Pyramiding and partial-profit plumbing. Optional so a client written before the
    // trim/pyramid layer still deserializes the book.
    // Legal pyramid adds already taken into this position (0, 1 or 2).
    int PyramidCount = 0,
    // The size the "Trim half" button sends: the largest slice at or below half that is a
    // whole board lot at the exit mark and leaves a whole entry lot behind. 0 when no legal
    // trim exists, so the UI hides the trim buttons instead of offering a rejected request.
    int RecommendedTrimShares = 0);

/// <summary>One settled trade in the closed ledger.</summary>
public sealed record ClosedTradeDto(
    int Id,
    string Ticker,
    string CompanyName,
    string Horizon,
    DateOnly OpenedDate,
    DateOnly? ClosedDate,
    int Shares,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal SellProceedsPhp,
    decimal RealizedNetPnlPhp,
    decimal? RealizedNetR,
    string Notes);

/// <summary>One point on the realized equity curve (closed trades only).</summary>
public sealed record PortfolioEquityPointDto(
    DateOnly Date,
    decimal EquityPhp,
    decimal DrawdownPct);

/// <summary>Exposure of one PSE sector against its caps, for the Book tab gauges.</summary>
public sealed record SectorExposureDto(
    string Sector,
    decimal ExposurePct,
    int PositionCount,
    decimal MaxSectorExposurePct,
    int MaxPositionsPerSector);

/// <summary>The whole book: settings, mark-to-market, heat and exit alerts.</summary>
public sealed record PortfolioSummaryDto(
    PortfolioAccountDto Account,
    decimal TotalEquityPhp,
    decimal InvestedCapitalPhp,
    decimal UnrealizedNetPnlPhp,
    decimal RealizedNetPnlPhp,
    decimal OpenRiskPhp,
    decimal OpenHeatPct,
    int OpenPositions,
    int ClosedTrades,
    decimal WinRate,
    IReadOnlyList<OpenPositionDto> Positions,
    IReadOnlyList<ClosedTradeDto> Closed,
    IReadOnlyList<PortfolioEquityPointDto> RealizedEquityCurve,
    IReadOnlyList<string> Alerts,
    // Drawdown governor and sector heat. Optional so the summary stays readable for a
    // client that predates the pod-shop layer.
    decimal DrawdownPct = 0m,
    decimal PeakEquityPhp = 0m,
    string RiskPosture = "NORMAL",
    IReadOnlyList<SectorExposureDto>? SectorExposure = null,
    // Closed-trade expectancy telemetry, all measured in R (units of the entry risk) so a
    // PHP 3,000 winner and a PHP 30,000 winner read on the same scale.
    /// <summary>Average realized R per closed trade; the edge's per-trade pulse.</summary>
    decimal ExpectancyR = 0m,
    /// <summary>Gross winning R divided by gross losing R; 0 without closed trades.</summary>
    decimal ProfitFactor = 0m,
    /// <summary>Average winner R divided by average loser R; null until both sides exist.</summary>
    decimal? PayoffRatio = null);

/// <summary>Partial account update; null fields leave the stored value untouched.</summary>
public sealed record PortfolioSettingsUpdateRequest(
    decimal? StartingCapitalPhp = null,
    decimal? CashBalancePhp = null,
    decimal? MaxRiskPerTradePct = null,
    decimal? SmallSizeRiskMultiplier = null,
    decimal? MaxPositionPct = null,
    decimal? MaxPortfolioHeatPct = null,
    decimal? MaxSectorExposurePct = null,
    int? MaxPositionsPerSector = null,
    decimal? SoftDrawdownThresholdPct = null,
    decimal? HardDrawdownThresholdPct = null,
    decimal? TargetAnnualVolPct = null);

/// <summary>Request to log a position ("Take Trade"). Prices must sit on the tick grid.</summary>
public sealed record OpenTradeRequest(
    string Ticker,
    int Shares,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TargetPrice,
    SignalHorizon Horizon = SignalHorizon.WEEK,
    EdgeVerdict EdgeVerdict = EdgeVerdict.TRADE,
    string? CompanyName = null,
    string? Sector = null,
    int? SignalItemId = null,
    DateOnly? OpenedDate = null,
    string? Notes = null);

/// <summary>Request to move the live stop (e.g. to the fee-paid break-even tick).</summary>
public sealed record UpdateStopRequest(decimal StopLoss);

/// <summary>Request to close a position at an actual fill price.</summary>
public sealed record CloseTradeRequest(
    decimal ExitPrice,
    DateOnly? ClosedDate = null,
    string? Notes = null);

/// <summary>
/// Request to bank part of an open position. Both the trimmed slice and the remaining shares
/// must be whole board lots at their own prices; the server does the proportional cost split.
/// </summary>
public sealed record TrimTradeRequest(
    int Shares,
    decimal ExitPrice,
    DateOnly? ClosedDate = null,
    string? Notes = null);

/// <summary>The reduced position plus the settled slice that landed in the closed ledger.</summary>
public sealed record TrimTradeResultDto(
    OpenPositionDto RemainingPosition,
    ClosedTradeDto SettledSlice);
