namespace PseSignalDesk.Application.Models;

/// <summary>
/// Aggregate grading summary for one selection cohort (either the LLM picks or the
/// deterministic Quant baseline) across all evaluated historical sessions.
/// </summary>
public sealed record PromptEvalCohortSummaryDto(
    int TotalPicks,
    int FilledPicks,
    int UnfilledPicks,
    decimal FillRate,
    decimal WinRate,
    decimal MeanRealizedNetR,
    decimal ProfitFactor,
    decimal BrierScore,
    int Top3Resolved,
    decimal Top3WinRate,
    decimal Top3MeanRealizedNetR);

/// <summary>
/// Per-session breakdown from a historical LLM-vs-Quant evaluation run.
/// </summary>
public sealed record PromptEvalSessionDto(
    DateOnly TradeDate,
    string Regime,
    int CandidateCount,
    int LlmPickCount,
    int QuantPickCount,
    decimal? SpearmanRho,
    decimal LlmMeanRealizedNetR,
    decimal QuantMeanRealizedNetR,
    int LatencyMs,
    int? PromptTokens,
    int? CompletionTokens);

/// <summary>
/// Walk-forward comparison of the LLM ranker against the deterministic Quant Top-10 baseline
/// over real historical sessions, using strictly causal prefixes and real forward OHLC bars.
/// </summary>
public sealed record PromptEvalResultDto(
    string Horizon,
    string Engine,
    string Model,
    int SessionsRequested,
    int SessionsEvaluated,
    PromptEvalCohortSummaryDto LlmSummary,
    PromptEvalCohortSummaryDto QuantBaselineSummary,
    decimal? MeanSpearmanRho,
    decimal DeltaWinRatePct,
    decimal DeltaMeanRealizedNetR,
    decimal DeltaBrierScore,
    int TotalPromptTokens,
    int TotalCompletionTokens,
    int TotalLatencyMs,
    IReadOnlyList<PromptEvalSessionDto> Sessions);
