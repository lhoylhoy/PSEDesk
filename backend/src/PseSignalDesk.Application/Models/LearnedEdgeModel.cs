using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Application.Models;

/// <summary>
/// The promoted money-manager model projected into exactly the shape the live edge engine
/// needs. Only a model that beat its neutral prior on held-out trades can be constructed
/// here, so the live path cannot accidentally price with a rejected fit.
/// </summary>
public sealed record LearnedEdgeModel(
    string Horizon,
    double Intercept,
    IReadOnlyDictionary<string, double> Weights,
    IReadOnlyDictionary<string, FeatureStat> Stats,
    decimal KellyFractionMultiplier)
{
    /// <summary>A learned tilt may move the odds by at most six probability points.</summary>
    public const decimal MaximumTilt = 0.06m;

    /// <summary>How much of the model's deviation from its own base rate is applied.</summary>
    public const decimal BlendWeight = 0.50m;

    /// <summary>The neutral full-Kelly fraction the Quarter-Kelly stake already represents.</summary>
    public const decimal NeutralKellyMultiplier = 0.25m;

    /// <summary>Builds the live view, or null when no promoted model exists for the horizon.</summary>
    public static LearnedEdgeModel? FromActive(TrainedHorizonModel? model)
    {
        if (model is null || !model.IsActive)
        {
            return null;
        }

        var weights = TrainedModelJson.ReadWeights(model.WeightsJson);
        var stats = TrainedModelJson.ReadStats(model.FeatureStatsJson);

        if (weights.Count == 0 || stats.Count == 0)
        {
            return null;
        }

        return new LearnedEdgeModel(
            model.Horizon.ToString(),
            (double)model.Intercept,
            weights,
            stats,
            model.OptimalKellyFractionMultiplier > 0m
                ? model.OptimalKellyFractionMultiplier
                : NeutralKellyMultiplier);
    }

    /// <summary>Probability the model assigns to one candidate's factor snapshot.</summary>
    public decimal Probability(decimal?[] features)
    {
        ArgumentNullException.ThrowIfNull(features);

        var eta = Intercept;

        for (var index = 0; index < TrainedModelFeatures.Length && index < features.Length; index++)
        {
            var key = TrainedModelFeatures.Keys[index];

            if (!Weights.TryGetValue(key, out var weight))
            {
                continue;
            }

            // A missing input is mean-imputed, which is exactly zero after standardization,
            // so it contributes nothing instead of inventing a level.
            var standardized = features[index] is decimal value &&
                               Stats.TryGetValue(key, out var stat) &&
                               stat.Scale > 0d
                ? ((double)value - stat.Mean) / stat.Scale
                : 0d;

            eta += weight * standardized;
        }

        return decimal.Round((decimal)Sigmoid(eta), 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>The model's own base rate: the probability of an average candidate.</summary>
    public decimal BaseProbability =>
        decimal.Round((decimal)Sigmoid(Intercept), 6, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Bounded probability tilt: half the model's deviation from its own base rate, capped at
    /// six points. A learned model may inform the odds, never dominate them.
    /// </summary>
    public decimal Tilt(decimal?[] features)
    {
        var deviation = (Probability(features) - BaseProbability) * BlendWeight;

        return decimal.Round(
            Math.Clamp(deviation, -MaximumTilt, MaximumTilt),
            4,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rescales the Quarter-Kelly stake so the learned full-Kelly fraction is what actually
    /// funds the trade: 0.25x leaves the stake untouched, 0.15x cuts it to 0.6x.
    /// </summary>
    public decimal KellyScale =>
        decimal.Round(KellyFractionMultiplier / NeutralKellyMultiplier, 4, MidpointRounding.AwayFromZero);

    private static double Sigmoid(double eta) =>
        eta >= 0d
            ? 1d / (1d + Math.Exp(-eta))
            : Math.Exp(eta) / (1d + Math.Exp(eta));
}
