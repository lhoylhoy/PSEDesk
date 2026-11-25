using System.Text.Json;
using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Application.Models;

/// <summary>
/// The twelve scale-free factor inputs the money-manager model learns on. Every value is a
/// ratio, a percentage or a bounded score, so the same weight means the same thing whether
/// it is applied to a PHP 1 small cap or a PHP 400 large cap. A missing input is kept null
/// and imputed to the training mean at inference time — it is never guessed as zero.
/// </summary>
public static class TrainedModelFeatures
{
    public const int Length = 12;

    /// <summary>Stable JSON keys. Order is the model's column order and must never change.</summary>
    public static readonly string[] Keys =
    {
        "relative_strength_63d",
        "volatility_contraction_ratio",
        "atr_pct",
        "up_down_volume_ratio_20d",
        "chaikin_money_flow_20d",
        "trend_efficiency_63d",
        "beta_252d",
        "max_drawdown_252d_pct",
        "stage_2",
        "above_sma_150",
        "rsi_distance_from_50",
        "log10_turnover_20d"
    };

    public static readonly string[] Labels =
    {
        "Relative strength vs PSEi (63D, pp)",
        "Volatility contraction ratio",
        "ATR14 as a share of price",
        "Up/down volume ratio (20D)",
        "Chaikin money flow (20D)",
        "Trend efficiency (63D)",
        "Beta vs PSEi (252D)",
        "52-week max drawdown",
        "Weinstein Stage 2",
        "Trading above the 150D average",
        "RSI14 distance from 50",
        "Log10 of 20D turnover"
    };

    public static string LabelFor(string key)
    {
        var index = Array.IndexOf(Keys, key);

        return index >= 0 ? Labels[index] : key;
    }

    /// <summary>Builds the vector from a stored metric row, using the same column order as <see cref="Keys"/>.</summary>
    public static decimal?[] FromMetric(StockDailyMetric metric)
    {
        ArgumentNullException.ThrowIfNull(metric);

        return FromValues(
            metric.RelativeStrength63d,
            metric.VolatilityContractionRatio,
            metric.AtrPct,
            metric.UpDownVolumeRatio20d,
            metric.ChaikinMoneyFlow20d,
            metric.TrendEfficiency63d,
            metric.Beta252d,
            metric.MaxDrawdown252dPct,
            string.IsNullOrWhiteSpace(metric.WeinsteinStage)
                ? null
                : metric.WeinsteinStage == "STAGE_2" ? 1m : 0m,
            metric.Sma150 is decimal sma150 ? (metric.ClosePrice > sma150 ? 1m : 0m) : null,
            metric.Rsi14 - 50m,
            metric.AvgTurnover20dPhp > 0m
                ? decimal.Round((decimal)Math.Log10((double)metric.AvgTurnover20dPhp), 6)
                : null);
    }

    /// <summary>
    /// Column-order guard for callers that already hold the raw inputs (the live edge
    /// engine). Keeping one builder means a feature can never drift out of alignment with
    /// the weights trained on it.
    /// </summary>
    public static decimal?[] FromValues(
        decimal? relativeStrength63d,
        decimal? volatilityContractionRatio,
        decimal? atrPct,
        decimal? upDownVolumeRatio20d,
        decimal? chaikinMoneyFlow20d,
        decimal? trendEfficiency63d,
        decimal? beta252d,
        decimal? maxDrawdown252dPct,
        decimal? stage2,
        decimal? aboveSma150,
        decimal? rsiDistanceFrom50,
        decimal? log10Turnover20d) =>
        new[]
        {
            relativeStrength63d,
            volatilityContractionRatio,
            atrPct,
            upDownVolumeRatio20d,
            chaikinMoneyFlow20d,
            trendEfficiency63d,
            beta252d,
            maxDrawdown252dPct,
            stage2,
            aboveSma150,
            rsiDistanceFrom50,
            log10Turnover20d
        };
}

/// <summary>Mean and scale of one feature, fitted on the training slice only.</summary>
public sealed record FeatureStat(double Mean, double Scale);

/// <summary>
/// Reads and writes the two JSON blobs stored on the trained model. Kept in one place so the
/// trainer, the live edge engine and the scorecard all parse the same contract.
/// </summary>
public static class TrainedModelJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static IReadOnlyDictionary<string, double> ReadWeights(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, double>>(json, Options)
                   ?? new Dictionary<string, double>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }
    }

    public static IReadOnlyDictionary<string, FeatureStat> ReadStats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, FeatureStat>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, FeatureStat>>(json, Options)
                   ?? new Dictionary<string, FeatureStat>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, FeatureStat>(StringComparer.Ordinal);
        }
    }

    public static string WriteWeights(IReadOnlyDictionary<string, double> weights) =>
        JsonSerializer.Serialize(weights, Options);

    public static string WriteStats(IReadOnlyDictionary<string, FeatureStat> stats) =>
        JsonSerializer.Serialize(stats, Options);
}
