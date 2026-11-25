using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// Sector rotation is the money-manager dial: it is computed only from the session's own
/// metrics, so it is deterministic, offline and reviewable. These tests pin the composite
/// score, the quadrant bands and the rule that a missing component renormalises the weights
/// instead of silently scoring the sector lower.
/// </summary>
public sealed class SectorRotationServiceTests
{
    [Fact]
    public void Compute_ranks_sectors_by_composite_score_and_labels_the_quadrant()
    {
        var metrics = new List<StockDailyMetric>
        {
            Metric("BDO", "Banks", close: 150m, sma50: 140m, stage: "STAGE_2", relativeStrength: 5m),
            Metric("BPI", "Banks", close: 120m, sma50: 110m, stage: "STAGE_2", relativeStrength: 5m),
            Metric("SMPH", "Property", close: 30m, sma50: 34m, stage: "STAGE_4", relativeStrength: -6m),
            Metric("ALI", "Property", close: 25m, sma50: 28m, stage: "STAGE_4", relativeStrength: -6m)
        };

        var rotation = SectorRotationService.Compute(metrics);

        Assert.Equal(2, rotation.Count);

        var banks = rotation[0];
        Assert.Equal("Banks", banks.Sector);
        Assert.Equal(SectorQuadrant.Leading, banks.Quadrant);
        Assert.Equal(2, banks.StockCount);
        Assert.Equal(100m, banks.AboveSma50Pct);
        Assert.Equal(100m, banks.Stage2Pct);
        // 30% breadth + 25% stage + 30% relative strength (75/100) over the 85% of weight that
        // reported: (30 + 25 + 22.5) / 0.85 = 91.2.
        Assert.Equal(91.2m, banks.CompositeScore);

        var property = rotation[1];
        Assert.Equal("Property", property.Sector);
        Assert.Equal(SectorQuadrant.Lagging, property.Quadrant);
        Assert.Equal(0m, property.AboveSma50Pct);
        Assert.Equal(7.1m, property.CompositeScore);
    }

    [Fact]
    public void Compute_renormalises_the_weights_when_a_component_is_unreported()
    {
        // No relative strength and no up/down volume history: only breadth and stage reported,
        // so 55% of the intended weight carries the whole score instead of 85%.
        var rotation = SectorRotationService.Compute(new List<StockDailyMetric>
        {
            Metric("TEL", "Telecom", close: 1_300m, sma50: 1_290m, stage: "STAGE_1", relativeStrength: null),
            Metric("GLO", "Telecom", close: 1_800m, sma50: 1_900m, stage: "STAGE_1", relativeStrength: null)
        });

        var telecom = Assert.Single(rotation);
        Assert.Equal(27.3m, telecom.CompositeScore);
        Assert.Equal(SectorQuadrant.Lagging, telecom.Quadrant);
        Assert.Null(telecom.AvgRelativeStrength63d);
        Assert.Null(telecom.AvgUpDownVolumeRatio);
    }

    [Fact]
    public void Compute_scores_the_accumulation_footprint()
    {
        // A sector whose names close near their highs on expanding volume: breadth + stage +
        // relative strength + a 3.5x up/down volume ratio all pull the same way.
        var rotation = SectorRotationService.Compute(new List<StockDailyMetric>
        {
            Metric("ICT", "Industrial", close: 400m, sma50: 380m, stage: "STAGE_2", relativeStrength: 4m, upDownVolume: 3.5m),
            Metric("PCOR", "Industrial", close: 30m, sma50: 29m, stage: "STAGE_2", relativeStrength: 4m, upDownVolume: 3.5m)
        });

        var industrial = Assert.Single(rotation);
        Assert.Equal(3.5m, industrial.AvgUpDownVolumeRatio);
        // (30 + 25 + 21 + 15) / 1.00 = 91, with every component at its ceiling.
        Assert.Equal(91m, industrial.CompositeScore);
        Assert.Equal(SectorQuadrant.Leading, industrial.Quadrant);
    }

    [Fact]
    public void Compute_ignores_metrics_without_a_sector_and_empty_input()
    {
        Assert.Empty(SectorRotationService.Compute(Array.Empty<StockDailyMetric>()));
        Assert.Empty(SectorRotationService.Compute(new List<StockDailyMetric>
        {
            Metric("XYZ", "  ", close: 10m, sma50: 9m, stage: "STAGE_2", relativeStrength: 1m)
        }));
    }

    [Fact]
    public void QuadrantFor_maps_the_composite_bands()
    {
        Assert.Equal(SectorQuadrant.Leading, SectorRotationService.QuadrantFor(60m));
        Assert.Equal(SectorQuadrant.Improving, SectorRotationService.QuadrantFor(59.9m));
        Assert.Equal(SectorQuadrant.Improving, SectorRotationService.QuadrantFor(45m));
        Assert.Equal(SectorQuadrant.Weakening, SectorRotationService.QuadrantFor(44.9m));
        Assert.Equal(SectorQuadrant.Weakening, SectorRotationService.QuadrantFor(30m));
        Assert.Equal(SectorQuadrant.Lagging, SectorRotationService.QuadrantFor(29.9m));
        Assert.Equal(SectorQuadrant.Lagging, SectorRotationService.QuadrantFor(0m));
    }

    [Fact]
    public void QuadrantFor_looks_up_one_sector_case_insensitively()
    {
        var rotation = SectorRotationService.Compute(new List<StockDailyMetric>
        {
            Metric("BDO", "Banks", close: 150m, sma50: 140m, stage: "STAGE_2", relativeStrength: 5m)
        });

        Assert.Equal(SectorQuadrant.Leading, SectorRotationService.QuadrantFor(rotation, "banks"));
        Assert.Equal(SectorQuadrant.Leading, SectorRotationService.QuadrantFor(rotation, " Banks "));
        Assert.Null(SectorRotationService.QuadrantFor(rotation, "Mining"));
        Assert.Null(SectorRotationService.QuadrantFor(rotation, null));
        Assert.Null(SectorRotationService.QuadrantFor(null, "Banks"));
    }

    [Fact]
    public void QuadrantMap_exposes_every_evaluated_sector()
    {
        var rotation = SectorRotationService.Compute(new List<StockDailyMetric>
        {
            Metric("BDO", "Banks", close: 150m, sma50: 140m, stage: "STAGE_2", relativeStrength: 5m),
            Metric("SMPH", "Property", close: 30m, sma50: 34m, stage: "STAGE_4", relativeStrength: -6m)
        });

        var map = SectorRotationService.QuadrantMap(rotation);

        Assert.Equal(2, map.Count);
        Assert.Equal(SectorQuadrant.Leading, map["BANKS"]);
        Assert.Equal(SectorQuadrant.Lagging, map["property"]);
        Assert.Empty(SectorRotationService.QuadrantMap(null));
    }

    private static StockDailyMetric Metric(
        string ticker,
        string sector,
        decimal close,
        decimal sma50,
        string? stage,
        decimal? relativeStrength,
        decimal? upDownVolume = null) =>
        new()
        {
            TradeDate = new DateOnly(2026, 2, 2),
            Ticker = ticker,
            CompanyName = $"{ticker} Holdings",
            Sector = sector,
            ClosePrice = close,
            Sma50 = sma50,
            WeinsteinStage = stage,
            RelativeStrength63d = relativeStrength,
            UpDownVolumeRatio20d = upDownVolume
        };
}
