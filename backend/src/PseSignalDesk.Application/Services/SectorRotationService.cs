using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Cross-sectional sector rotation, computed purely from the session's evaluated
/// <c>StockDailyMetric</c> rows. It is the sector dial of the money-manager loop: which PSE
/// clusters are being accumulated (leading/improving) and which are being distributed
/// (weakening/lagging). No network call, no model, no new data source — the same metrics the
/// run already priced.
/// </summary>
public static class SectorRotationService
{
    /// <summary>Weight of breadth (share above the 50D average) in the composite score.</summary>
    public const decimal BreadthWeight = 0.30m;

    /// <summary>Weight of the Stage 2 (advancing) share in the composite score.</summary>
    public const decimal StageWeight = 0.25m;

    /// <summary>Weight of mean 63-session relative strength in the composite score.</summary>
    public const decimal RelativeStrengthWeight = 0.30m;

    /// <summary>Weight of the accumulation footprint (up/down volume) in the composite score.</summary>
    public const decimal AccumulationWeight = 0.15m;

    /// <summary>
    /// Groups the session's metrics by their PSE sector and ranks them by composite score.
    /// Sectors with no metric are absent rather than zero-scored; a sector with one name is
    /// reported with its (noisy) StockCount so the reader can discount it.
    /// </summary>
    public static IReadOnlyList<SectorRotationDto> Compute(IReadOnlyList<StockDailyMetric> metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            return Array.Empty<SectorRotationDto>();
        }

        return metrics
            .Where(metric => !string.IsNullOrWhiteSpace(metric.Sector))
            .GroupBy(metric => metric.Sector.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => Build(group.Key, group.ToList()))
            .OrderByDescending(rotation => rotation.CompositeScore)
            .ThenBy(rotation => rotation.Sector, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Sector → quadrant lookup for scoring and the prompt.</summary>
    public static IReadOnlyDictionary<string, string> QuadrantMap(IReadOnlyList<SectorRotationDto>? rotation)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rotation is null)
        {
            return map;
        }

        foreach (var entry in rotation)
        {
            map[entry.Sector] = entry.Quadrant;
        }

        return map;
    }

    /// <summary>Quadrant of one sector, or null when the sector was not evaluated today.</summary>
    public static string? QuadrantFor(IReadOnlyList<SectorRotationDto>? rotation, string? sector)
    {
        if (rotation is null || string.IsNullOrWhiteSpace(sector))
        {
            return null;
        }

        return rotation
            .FirstOrDefault(entry => string.Equals(entry.Sector, sector.Trim(), StringComparison.OrdinalIgnoreCase))
            ?.Quadrant;
    }

    private static SectorRotationDto Build(string sector, IReadOnlyList<StockDailyMetric> members)
    {
        var count = members.Count;
        var above50 = members.Count(metric => metric.Sma50 > 0m && metric.ClosePrice >= metric.Sma50);
        var stage2 = members.Count(metric =>
            string.Equals(metric.WeinsteinStage, "STAGE_2", StringComparison.Ordinal));

        var relativeStrength = Average(members.Select(metric => metric.RelativeStrength63d));
        var accumulation = Average(members.Select(metric => metric.UpDownVolumeRatio20d));

        var above50Pct = decimal.Round((decimal)above50 / count * 100m, 1, MidpointRounding.AwayFromZero);
        var stage2Pct = decimal.Round((decimal)stage2 / count * 100m, 1, MidpointRounding.AwayFromZero);

        // Renormalise the weights over the components that actually reported, so a sector
        // whose names have no relative-strength history is not silently scored 30% lower.
        var weighted = 0m;
        var totalWeight = 0m;

        weighted += above50Pct * BreadthWeight;
        totalWeight += BreadthWeight;

        weighted += stage2Pct * StageWeight;
        totalWeight += StageWeight;

        if (relativeStrength is decimal rs)
        {
            // -10 percentage points of excess return scores 0, +10 scores 100.
            weighted += Math.Clamp(50m + (rs * 5m), 0m, 100m) * RelativeStrengthWeight;
            totalWeight += RelativeStrengthWeight;
        }

        if (accumulation is decimal ratio)
        {
            // 0.5x up/down volume scores 0, 2.0x scores 100.
            weighted += Math.Clamp((ratio - 0.5m) / 1.5m * 100m, 0m, 100m) * AccumulationWeight;
            totalWeight += AccumulationWeight;
        }

        var composite = totalWeight <= 0m
            ? 0m
            : decimal.Round(weighted / totalWeight, 1, MidpointRounding.AwayFromZero);

        return new SectorRotationDto(
            sector,
            count,
            relativeStrength,
            above50Pct,
            stage2Pct,
            accumulation,
            composite,
            QuadrantFor(composite));
    }

    /// <summary>Quadrant bands: LEADING ≥ 60, IMPROVING 45-60, WEAKENING 30-45, else LAGGING.</summary>
    public static string QuadrantFor(decimal compositeScore) => compositeScore switch
    {
        >= 60m => SectorQuadrant.Leading,
        >= 45m => SectorQuadrant.Improving,
        >= 30m => SectorQuadrant.Weakening,
        _ => SectorQuadrant.Lagging
    };

    private static decimal? Average(IEnumerable<decimal?> values)
    {
        var reported = values.Where(value => value is not null).Select(value => value!.Value).ToList();

        return reported.Count == 0
            ? null
            : decimal.Round(reported.Average(), 3, MidpointRounding.AwayFromZero);
    }
}
