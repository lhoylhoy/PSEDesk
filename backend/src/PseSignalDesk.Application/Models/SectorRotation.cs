namespace PseSignalDesk.Application.Models;

/// <summary>
/// Cross-sectional rotation read for one PSE sector, computed from the same evaluated
/// <c>StockDailyMetric</c> rows the run already priced. It answers "is money rotating into
/// this cluster or out of it?" — the sector dial the macro playbook asks for — without a
/// single extra HTTP request.
/// </summary>
/// <param name="Sector">PSE sector label exactly as the catalog publishes it.</param>
/// <param name="StockCount">Names that contributed to the read, so a one-name "sector" is visible.</param>
/// <param name="AvgRelativeStrength63d">Mean 63-session excess return versus the PSEi, in points.</param>
/// <param name="AboveSma50Pct">Share of the sector trading at or above its own 50D average.</param>
/// <param name="Stage2Pct">Share of the sector in Weinstein Stage 2 (advancing).</param>
/// <param name="AvgUpDownVolumeRatio">Mean up/down volume ratio; null when no name reports it.</param>
/// <param name="CompositeScore">0-100 blend of the four dials above.</param>
/// <param name="Quadrant">LEADING, IMPROVING, WEAKENING or LAGGING.</param>
public sealed record SectorRotationDto(
    string Sector,
    int StockCount,
    decimal? AvgRelativeStrength63d,
    decimal? AboveSma50Pct,
    decimal? Stage2Pct,
    decimal? AvgUpDownVolumeRatio,
    decimal CompositeScore,
    string Quadrant);

/// <summary>Canonical quadrant labels, so the UI and the prompt never invent their own.</summary>
public static class SectorQuadrant
{
    public const string Leading = "LEADING";
    public const string Improving = "IMPROVING";
    public const string Weakening = "WEAKENING";
    public const string Lagging = "LAGGING";
}
