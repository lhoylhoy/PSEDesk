namespace PseSignalDesk.Application.Models;

/// <summary>
/// Deterministic participation snapshot of the evaluated universe on one session.
/// Percentages are null when the evaluated sample is empty, so the prompt and the UI
/// can say "unreported" instead of inventing a 0% tape.
/// </summary>
public sealed record MarketBreadth(
    decimal? AboveSma50Pct,
    decimal? AboveSma200Pct,
    int Stage2Count,
    int EvaluatedStockCount);
