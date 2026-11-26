using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The guard for the indicator micro-refactors. Every field of <see cref="StockDailyMetric"/> is
/// snapshotted at many prefix lengths on seeded series, including the live foreign-flow pillar
/// and the replay path that skips the empirical barrier study. A single changed decimal — an EMA
/// seed, an ATR smoothing step, a VCP ratio, a beta slope — fails the test.
/// </summary>
public sealed class QuantitativeFilterGoldenTests
{
    /// <summary>Prefixes that cross every indicator window (200, 213 … 408) plus the full series.</summary>
    private static readonly int[] PrefixLengths = Enumerable
        .Range(0, 17)
        .Select(step => 200 + (step * 13))
        .Append(SyntheticMarketFixture.BarCount)
        .ToArray();

    [Fact]
    public void EvaluateStock_output_matches_the_recorded_snapshot()
    {
        var service = new QuantitativeFilterService();
        var batch = SyntheticMarketFixture.BuildBatch();
        var indexBars = batch.IndexBars!;
        var rows = new List<GoldenMetricRow>();

        foreach (var stock in batch.Stocks.Take(3))
        {
            var bars = stock.Bars;

            foreach (var prefix in PrefixLengths)
            {
                rows.Add(new GoldenMetricRow(
                    stock.Ticker,
                    prefix,
                    "default",
                    service.EvaluateStock(
                        stock with { Bars = bars.Take(prefix).ToList() },
                        bars[prefix - 1].Date,
                        indexBars.Take(prefix).ToList())));
            }
        }

        var reported = batch.Stocks[0];
        rows.Add(new GoldenMetricRow(
            reported.Ticker,
            reported.Bars.Count,
            "with-foreign-flow",
            service.EvaluateStock(
                reported with { NetForeignTodayPhp = 12_000_000m },
                reported.Bars[^1].Date,
                indexBars)));

        rows.Add(new GoldenMetricRow(
            reported.Ticker,
            reported.Bars.Count,
            "replay-prefix",
            service.EvaluateStock(
                reported,
                reported.Bars[^1].Date,
                indexBars,
                computeEmpiricalBaseRate: false,
                includeLiveForeignFlow: false)));

        Assert.All(rows, row => Assert.NotNull(row.Metric));
        GoldenSnapshot.AssertMatches(
            "quantitative_filter_metrics.json",
            GoldenSnapshot.Serialize(rows));
    }

    /// <summary>One snapshotted metric: which ticker, which prefix and which call variant produced it.</summary>
    public sealed record GoldenMetricRow(string Ticker, int PrefixLength, string Variant, StockDailyMetric? Metric);
}
