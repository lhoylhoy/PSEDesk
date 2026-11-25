namespace PseSignalDesk.Application.Models;

/// <summary>One sector's realized record in the desk's own forward ledger.</summary>
public sealed record SectorMemoryDto(
    string Sector,
    int Samples,
    decimal WinRate,
    decimal AverageRealizedNetR);

/// <summary>One resolved stop-out, kept as a post-mortem line for the prompt.</summary>
public sealed record LossPostMortemDto(
    string Ticker,
    DateOnly RunDate,
    decimal EntryPrice,
    decimal StopLoss,
    decimal RealizedNetR,
    int BarsHeld);

/// <summary>
/// The desk's memory of its own results, fed back into the model prompt. It is computed from
/// the stored forward ledger only — never from the model's claims — so the prompt can say
/// "here is what actually happened" and the terminal can audit the same numbers.
/// </summary>
public sealed record DeskMemorySummary(
    string Horizon,
    int ResolvedCount,
    decimal WinRate,
    decimal ExpectancyR,
    decimal BrierScore,
    decimal CalibrationBiasPct,
    IReadOnlyList<SectorMemoryDto> BestSectors,
    IReadOnlyList<SectorMemoryDto> WorstSectors,
    IReadOnlyList<LearnedFactorWeightDto> TopFactors,
    IReadOnlyList<LossPostMortemDto> RecentStopOuts)
{
    public static DeskMemorySummary Empty(string horizon) => new(
        horizon,
        0,
        0m,
        0m,
        0m,
        0m,
        Array.Empty<SectorMemoryDto>(),
        Array.Empty<SectorMemoryDto>(),
        Array.Empty<LearnedFactorWeightDto>(),
        Array.Empty<LossPostMortemDto>());
}
