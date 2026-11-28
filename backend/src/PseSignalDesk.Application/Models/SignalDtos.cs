namespace PseSignalDesk.Application.Models;

/// <summary>
/// API contracts returned by <c>/api/signals/*</c>. Enums are exposed as strings
/// (NEW, HELD, UPGRADED, DOWNGRADED, INVALIDATED, TARGET_HIT, DISPLACED) so the
/// React terminal can switch on them directly.
/// </summary>
/// <summary>
/// Server-computed order ticket for one pick: the whole board lots the desk's own risk
/// budget funds against the current account equity, the all-in buy outlay including the
/// PSE buy-leg fees, the fee-paid peso risk and reward, and the break-even stop tick. The
/// browser must render these numbers rather than re-derive them.
/// </summary>
public sealed record OrderTicketDto(
    int BoardLotSize,
    int Shares,
    int Lots,
    decimal BuyOutlayPhp,
    decimal NetRiskPhp,
    decimal NetRewardPhp,
    decimal BreakEvenStopPrice,
    decimal RiskBudgetPhp,
    bool LiquidityCapped,
    IReadOnlyList<string> Warnings,
    // Pod-shop risk governance and PSE transaction-cost analysis. Appended with defaults so
    // rows/tickets produced before the governor existed still deserialize and render.
    decimal EffectiveRiskPct = 0m,
    string RiskGovernor = "NORMAL",
    decimal DrawdownPct = 0m,
    decimal EdgeMultiplier = 1m,
    decimal VolatilityMultiplier = 1m,
    decimal DrawdownMultiplier = 1m,
    decimal SlippageRatePct = 0m,
    decimal EstimatedSlippagePhp = 0m,
    decimal NetRiskAfterSlippagePhp = 0m,
    decimal NetRewardAfterSlippagePhp = 0m,
    decimal AdvParticipationPct = 0m,
    string ExecutionGuidance = "LIQUID_MARKETABLE");

/// <summary>
/// The desk's one-line call for regular buyers: a traffic-light instruction plus the
/// 10-90 directional score behind it, the plain reasons and the sectors money is moving
/// into or out of. Every field is computed server-side from five deterministic pillars
/// (index trend, breadth, rotation, foreign flow, volatility/FX); the browser only renders.
/// </summary>
public sealed record MarketDirectionForecastDto(
    /// <summary>RIDE_THE_WAVE, BE_SELECTIVE or STAY_IN_CASH.</summary>
    string Call,
    /// <summary>UPTREND, SIDEWAYS or DOWNTREND.</summary>
    string Direction,
    /// <summary>Bullish tilt from 10 (deeply bearish) to 90 (strongly bullish); 50 is neutral.</summary>
    int BullishScore,
    /// <summary>HIGH, MODERATE or CAUTIOUS, from how many pillars reported and agreed.</summary>
    string Confidence,
    string Headline,
    string ActionSummary,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> LeadingSectors,
    IReadOnlyList<string> LaggingSectors);

/// <summary>
/// The four-step exit roadmap for one pick, all prices tick-valid for the PSE's own grid:
/// where to buy, where the idea is wrong, when to lock break-even, and where to trail.
/// A <c>null</c> optional price means the wave never reached that leg — never a zero.
/// </summary>
public sealed record WaveTradePlanDto(
    decimal IdealBuyPrice,
    decimal CutLossPrice,
    decimal BreakEvenStopPrice,
    decimal TargetPrice,
    decimal MaxChasePrice,
    decimal LockBreakEvenTriggerPrice,
    decimal TrailWaveTriggerPrice,
    decimal? TrailStopPrice,
    /// <summary>READY_NOW, NEAR_ENTRY, EXTENDED_WAIT_PULLBACK, BROKEN_BELOW_STOP or WAIT_WATCH_ONLY.</summary>
    string EntryStatus,
    int MaxHoldTradingDays);

/// <summary>
/// The desk's own report card, surfaced where a beginner actually reads it: how many picks
/// have been graded, the win rate, the average R and the profit factor. <c>IsCalibrated</c>
/// only turns true once enough picks have resolved for the numbers to mean anything.
/// </summary>
public sealed record TrustPulseDto(
    string Horizon,
    int TotalPicks,
    int ResolvedCount,
    int OpenCount,
    decimal WinRate,
    decimal ExpectancyR,
    decimal ProfitFactor,
    bool IsCalibrated);

public sealed record SignalItemDto(
    int Rank,
    string Ticker,
    string CompanyName,
    string Sector,
    string DriftStatus,
    int? PreviousRank,
    int ConvictionScore,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TargetPrice,
    decimal RiskRewardRatio,
    string Thesis,
    string DeviationReason,
    TechnicalMetricsDto? Metrics,
    IReadOnlyList<StockDisclosureDto> Disclosures,
    int BoardLotSize,
    int BoardLotShares,
    IReadOnlyList<string> Citations,
    IReadOnlyList<string> TrapFlags,
    decimal NetRiskRewardRatio,
    decimal WinProbability,
    decimal BreakEvenWinRate,
    decimal ExpectedValueR,
    decimal QuarterKellyFraction,
    string EdgeVerdict,
    string BearCase,
    string OutcomeStatus,
    DateOnly? OutcomeDate,
    decimal? OutcomeExitPrice,
    decimal? RealizedNetR,
    int? BarsHeld,
    OrderTicketDto? OrderTicket = null,
    // Ride-the-wave exit roadmap. Appended with a default so stored runs and older clients
    // keep deserializing; a null plan simply hides the roadmap.
    WaveTradePlanDto? WavePlan = null,
    // Deterministic screen context for this pick: the score it carried on the run date and its
    // ordinal in the quant table the model was shown. Appended with defaults so older clients
    // still deserialize; zero/null means the run predates quant provenance.
    int QuantPreScore = 0,
    int? QuantRank = null);

public sealed record SignalDropDto(
    string Ticker,
    string CompanyName,
    int? PreviousRank,
    string DropCategory,
    string Reason);

public sealed record SignalRunDto(
    int Id,
    DateOnly RunDate,
    string Horizon,
    DateTime CreatedAtUtc,
    IReadOnlyList<SignalItemDto> Items,
    IReadOnlyList<SignalDropDto> Drops,
    decimal RecommendedCashPct,
    int TradeCount,
    int SmallSizeCount,
    int WatchOnlyCount,
    MarketBreadthDto? Breadth,
    // How the book was produced (model, prompt fingerprint, sampling settings, cost). Null on
    // runs stored before provenance tracking, so an old run simply shows no provenance.
    ModelProvenanceDto? ModelProvenance = null,
    IReadOnlyList<string>? DataQualityWarnings = null);

/// <summary>
/// The provenance of one generation: which engine and model, the prompt fingerprint, the
/// sampling settings that shaped the answer, and what the call cost. Audit data, not advice.
/// </summary>
public sealed record ModelProvenanceDto(
    string Engine,
    string Model,
    string PromptHash,
    decimal Temperature,
    int? Seed,
    int RetryCount,
    int LatencyMs,
    int? PromptTokens,
    int? CompletionTokens,
    int PromptCharacters,
    bool SchemaConstrained,
    bool FastExtractionApplied);

public sealed record MarketBreadthDto(
    decimal? AboveSma50Pct,
    decimal? AboveSma200Pct,
    int Stage2Count,
    int EvaluatedStockCount);

public sealed record TechnicalMetricsDto(
    decimal ClosePrice,
    decimal ChangePct,
    decimal AvgTurnover20dPhp,
    decimal VolumeRatioVs20d,
    decimal Rsi14,
    decimal Ema20,
    decimal Sma50,
    decimal Sma200,
    decimal Atr14,
    decimal SupportLevel,
    decimal ResistanceLevel,
    decimal? NetForeignTodayPhp,
    int QuantPreScore,
    decimal? Sma150,
    decimal? Week52High,
    string? WeinsteinStage,
    decimal? OpenPrice,
    decimal? HighPrice,
    decimal? LowPrice,
    decimal? RelativeStrength63d,
    decimal? AtrPct,
    decimal? VolatilityContractionRatio,
    decimal? EmpiricalWinRate,
    decimal? TrailingDividendPerShare = null,
    decimal? TrailingDividendYieldPct = null,
    DateOnly? LastExDividendDate = null,
    // Institutional factor sleeve inputs. Optional so runs stored before the money-manager
    // migration still map; a null means "unreported", never zero.
    decimal? EmpiricalWinRateDay = null,
    decimal? EmpiricalWinRateMonth = null,
    decimal? EmpiricalWinRateYear = null,
    decimal? UpDownVolumeRatio20d = null,
    decimal? ChaikinMoneyFlow20d = null,
    decimal? TrendEfficiency63d = null,
    decimal? Beta252d = null,
    decimal? MaxDrawdown252dPct = null,
    decimal? TickSpreadRatePct = null);

public sealed record StockDisclosureDto(
    DateOnly Date,
    string Headline,
    string Category,
    string Source,
    string TrapFlag = "NONE");

public sealed record MarketRegimeDto(
    DateOnly TradeDate,
    decimal PseiClose,
    decimal PseiSma50,
    decimal PseiSma200,
    string Sma50Status,
    string Sma200Status,
    decimal? NetForeignFlowPhp,
    decimal UsdPhp,
    string Regime,
    string RegimeNote,
    decimal? BreadthAboveSma50Pct,
    decimal? BreadthAboveSma200Pct,
    int? Stage2Count,
    int? EvaluatedStockCount,
    // Macro, FX and rotation dials. Appended with defaults so snapshots stored before the
    // money-manager migration keep mapping to the same contract.
    decimal? UsdPhpSma50 = null,
    decimal? UsdPhpChange20dPct = null,
    decimal? PseiRealizedVol20dPct = null,
    IReadOnlyList<SectorRotationDto>? SectorRotation = null,
    // Cross-asset macro dials and the foreign-flow provenance. Appended with defaults so a
    // stored snapshot written before the macro feeds existed still maps to this contract.
    decimal? Us10YClosePct = null,
    decimal? Us10YChange20dBps = null,
    decimal? BrentUsd = null,
    decimal? BrentChange20dPct = null,
    decimal? EpheCloseUsd = null,
    decimal? EpheChange20dPct = null,
    string? ForeignFlowSource = null,
    // The plain-language traffic light derived from this snapshot plus today's run.
    // Null on responses produced before the direction service existed.
    MarketDirectionForecastDto? DirectionForecast = null);

public sealed record DateOptionDto(
    DateOnly Date,
    bool HasMarketSnapshot,
    bool HasSignalRun);

public sealed record TickerHistoryPointDto(
    DateOnly RunDate,
    int Rank,
    string DriftStatus,
    int? PreviousRank,
    int ConvictionScore,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TargetPrice,
    decimal RiskRewardRatio,
    decimal NetRiskRewardRatio,
    decimal WinProbability,
    decimal ExpectedValueR,
    string EdgeVerdict,
    string OutcomeStatus,
    decimal? RealizedNetR);

public sealed record TickerHistoryDto(
    string Ticker,
    string CompanyName,
    string Sector,
    IReadOnlyList<TickerHistoryPointDto> Points);

/// <summary>
/// Envelope for the terminal. <see cref="Run"/> is null when a date has market data
/// but no pipeline run yet, which lets the UI show the regime strip and a CTA.
/// </summary>
public sealed record LatestSignalsResponse(
    SignalRunDto? Run,
    MarketRegimeDto? Regime,
    IReadOnlyList<DateOptionDto> Dates,
    IReadOnlyList<SignalDropDto> DroppedFromYesterday,
    // The desk's own graded track record for this horizon. Null when the ledger could not
    // be read — the terminal then hides the pulse instead of inventing a win rate.
    TrustPulseDto? TrustPulse = null);

/// <summary>
/// One probability bucket of the calibration table: what the desk predicted versus
/// what actually happened.
/// </summary>
public sealed record CalibrationBucketDto(
    string Label,
    decimal PredictedWinPct,
    decimal ActualWinPct,
    int SampleCount);

/// <summary>
/// Realized performance split by the server-owned actionability verdict, so the desk
/// can prove (or disprove) that TRADE picks beat WATCH_ONLY picks.
/// </summary>
public sealed record VerdictBreakdownDto(
    string Verdict,
    int Count,
    int ResolvedCount,
    decimal WinRate,
    decimal AverageRealizedNetR,
    decimal TotalRealizedNetR);

/// <summary>
/// One graded pick from the forward-outcome ledger.
/// </summary>
public sealed record ResolvedSignalDto(
    string Ticker,
    DateOnly RunDate,
    string Horizon,
    string EdgeVerdict,
    decimal WinProbability,
    decimal ExpectedValueR,
    string OutcomeStatus,
    DateOnly? OutcomeDate,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TargetPrice,
    decimal? OutcomeExitPrice,
    decimal? RealizedNetR,
    int? BarsHeld);

/// <summary>
/// Did the model add anything over the deterministic screen? Compares the model's order with
/// the quant table the model was handed, on the picks both actually saw: the mean rank
/// correlation across runs, and the realized record of each side's top three. Null when no run
/// carries a stored quant rank yet.
/// </summary>
public sealed record RankingQualityDto(
    int RunsMeasured,
    int PicksWithQuantRank,
    decimal? MeanSpearmanRho,
    int ModelTop3Resolved,
    decimal ModelTop3WinRate,
    decimal ModelTop3AverageRealizedNetR,
    int QuantTop3Resolved,
    decimal QuantTop3WinRate,
    decimal QuantTop3AverageRealizedNetR);

/// <summary>
/// Forward-outcome scorecard for one horizon: win rate, expectancy, profit factor,
/// Brier calibration and the verdict breakdown, plus the model-vs-quant ranking read.
/// </summary>
public sealed record SignalScorecardDto(
    string Horizon,
    int TotalPicks,
    int ResolvedCount,
    int OpenCount,
    decimal WinRate,
    decimal AverageRealizedNetR,
    decimal ExpectancyR,
    decimal ProfitFactor,
    decimal BrierScore,
    IReadOnlyList<CalibrationBucketDto> Calibration,
    IReadOnlyList<VerdictBreakdownDto> Verdicts,
    IReadOnlyList<ResolvedSignalDto> RecentResolved,
    // Appended with a default so responses shaped before the ranking read existed still map.
    RankingQualityDto? RankingQuality = null);

