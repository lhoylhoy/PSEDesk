using System.Text.Json.Serialization;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Models;

/// <summary>
/// Strongly typed view of the model's strict JSON contract.
/// </summary>
public sealed class OllamaSignalOutput
{
    [JsonPropertyName("top_10")]
    public List<OllamaTopPick> Top10 { get; set; } = new();

    [JsonPropertyName("dropped_from_yesterday")]
    public List<OllamaDrop> DroppedFromYesterday { get; set; } = new();
}

public sealed class OllamaTopPick
{
    /// <summary>
    /// Ticker of the pick. The array order of <see cref="OllamaSignalOutput.Top10"/> is the
    /// ranking: the server assigns rank 1..n itself, so the model is no longer asked to echo a
    /// rank the desk would overwrite.
    /// </summary>
    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [JsonPropertyName("conviction_score")]
    public decimal ConvictionScore { get; set; }

    [JsonPropertyName("entry_price")]
    public decimal EntryPrice { get; set; }

    [JsonPropertyName("stop_loss")]
    public decimal StopLoss { get; set; }

    [JsonPropertyName("target_price")]
    public decimal TargetPrice { get; set; }

    [JsonPropertyName("thesis")]
    public string Thesis { get; set; } = string.Empty;

    [JsonPropertyName("deviation_reason")]
    public string? DeviationReason { get; set; }

    /// <summary>
    /// Knowledge-base references the model used for the thesis, e.g.
    /// <c>["pse_rules#ex-dividend-price-drops","minervini#vcp"]</c>. Validated against the
    /// retrieved snippets before it is persisted.
    /// </summary>
    [JsonPropertyName("cited_rules")]
    public List<string>? CitedRules { get; set; }

    /// <summary>
    /// Mandatory pre-mortem: the single most likely way this setup fails, in at most 30
    /// words. When it is missing the server substitutes a deterministic fallback built
    /// from the stop, the resistance level and the trend stage.
    /// </summary>
    [JsonPropertyName("bear_case")]
    public string? BearCase { get; set; }
}

public sealed class OllamaDrop
{
    [JsonPropertyName("ticker")]
    public string Ticker { get; set; } = string.Empty;

    [JsonPropertyName("previous_rank")]
    public int? PreviousRank { get; set; }

    [JsonPropertyName("drop_category")]
    public string DropCategory { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// What one generation actually cost and how it was produced. Persisted with the run so the
/// ranking can be compared across models, prompts and settings instead of guessed at.
/// </summary>
public sealed record OllamaGenerationMetadata(
    string EngineName,
    string Model,
    string PromptHash,
    decimal Temperature,
    int? Seed,
    int PromptCharacters,
    int? PromptTokens,
    int? CompletionTokens,
    int RetryCount,
    int LatencyMs,
    bool SchemaConstrained,
    bool FastExtractionApplied);

/// <summary>
/// The engine's answer plus the provenance of the call that produced it.
/// </summary>
public sealed record OllamaSignalResult(
    OllamaSignalOutput Output,
    OllamaGenerationMetadata Metadata);

/// <summary>
/// The request sent into the signal engine. Kept provider-agnostic so test doubles
/// can implement the same contract.
/// </summary>
public sealed record OllamaSignalRequest(
    DateOnly TradeDate,
    SignalHorizon Horizon,
    MarketRegime Regime,
    string RegimeNote,
    decimal PseiClose,
    decimal? NetForeignFlowPhp,
    decimal UsdPhp,
    IReadOnlyList<SignalCandidateInput> Candidates,
    IReadOnlyList<PriorSignalInput> YesterdayTop10,
    IReadOnlyList<KnowledgeSnippet>? ReferencePlaybooks = null,
    string? FastExtractionSummary = null,
    MarketBreadth? Breadth = null,
    IReadOnlyList<SectorRotationDto>? SectorRotation = null,
    // Cross-asset macro context and foreign-flow provenance. Optional and nullable: the
    // prompt prints "n/a" for any unreported dial instead of a fabricated zero.
    decimal? Us10YClosePct = null,
    decimal? Us10YChange20dBps = null,
    decimal? BrentUsd = null,
    decimal? BrentChange20dPct = null,
    decimal? EpheCloseUsd = null,
    decimal? EpheChange20dPct = null,
    string? ForeignFlowSource = null,
    // The desk's own graded record plus the model that currently prices the book. Both are
    // optional: a desk with no resolved picks prints nothing instead of inventing a record.
    DeskMemorySummary? DeskMemory = null,
    TrainedModelSummaryDto? LearnedModel = null);

public sealed record SignalCandidateInput(
    string Ticker,
    string CompanyName,
    string Sector,
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
    IReadOnlyList<StockDisclosure> Disclosures,
    int BoardLot = 0,
    decimal StaticCeiling = 0m,
    decimal StaticFloor = 0m,
    IReadOnlyList<PseTrapFlag>? TrapFlags = null,
    decimal? Sma150 = null,
    decimal? Week52High = null,
    string? WeinsteinStage = null,
    decimal? RelativeStrength63d = null,
    decimal? AtrPct = null,
    decimal? VolatilityContractionRatio = null,
    decimal? EmpiricalWinRate = null,
    decimal? TrailingDividendPerShare = null,
    decimal? TrailingDividendYieldPct = null,
    DateOnly? LastExDividendDate = null,
    // Institutional factor sleeves reported to the model. Null means "not reported" and must
    // never be rendered as zero.
    decimal? EmpiricalWinRateDay = null,
    decimal? EmpiricalWinRateMonth = null,
    decimal? EmpiricalWinRateYear = null,
    decimal? UpDownVolumeRatio20d = null,
    decimal? ChaikinMoneyFlow20d = null,
    decimal? TrendEfficiency63d = null,
    decimal? Beta252d = null,
    decimal? MaxDrawdown252dPct = null,
    string? SectorQuadrant = null,
    decimal? TickSpreadPct = null);

public sealed record PriorSignalInput(
    string Ticker,
    int Rank,
    int ConvictionScore,
    decimal EntryPrice,
    decimal StopLoss,
    decimal TargetPrice,
    string Thesis);
