using PseSignalDesk.Domain.Entities;

namespace PseSignalDesk.Application.Models;

/// <summary>One learned factor, ranked by how much one standard deviation of it moves the odds.</summary>
public sealed record LearnedFactorWeightDto(
    string Feature,
    string Label,
    decimal Weight,
    decimal NormalizedImpactPct,
    string Direction);

/// <summary>
/// The desk's report card on its own learned model: what it was fitted on, what it scored
/// out of sample, whether it earned the right to price live picks, and the factors it leans
/// on. A null <c>TrainedAtUtc</c> means no model was ever fitted for the horizon.
/// </summary>
public sealed record TrainedModelSummaryDto(
    string Horizon,
    string Status,
    bool IsActive,
    DateTime? TrainedAtUtc,
    DateOnly? TrainStartDate,
    DateOnly? TrainEndDate,
    DateOnly? ValidationStartDate,
    DateOnly? ValidationEndDate,
    int TrainSampleCount,
    int ValidationSampleCount,
    int EmbargoBars,
    decimal BaselineBrierScore,
    decimal TrainedBrierScore,
    decimal BrierImprovementPct,
    decimal BaselineLogLoss,
    decimal TrainedLogLoss,
    decimal ValidationWinRate,
    decimal ValidationExpectancyR,
    decimal ValidationProfitFactor,
    decimal OptimalKellyFractionMultiplier,
    IReadOnlyList<LearnedFactorWeightDto> Weights,
    string Summary);

/// <summary>Status vocabulary shared by the API, the prompt and the terminal.</summary>
public static class TrainedModelStatus
{
    /// <summary>No model row exists yet; the deterministic prior prices every pick.</summary>
    public const string Untrained = "UNTRAINED";

    /// <summary>The held-out score beat the neutral baseline and the sample gates passed.</summary>
    public const string Learned = "LEARNED";

    /// <summary>A model was fitted but failed the safety gate, so live pricing stays on the prior.</summary>
    public const string PriorFallback = "PRIOR_FALLBACK";
}

/// <summary>Maps the persisted model onto the read contract used by the API and the prompt.</summary>
public static class TrainedModelPresenter
{
    public static TrainedModelSummaryDto ToDto(TrainedHorizonModel? model, string horizon)
    {
        if (model is null)
        {
            return new TrainedModelSummaryDto(
                horizon,
                TrainedModelStatus.Untrained,
                false,
                null,
                null,
                null,
                null,
                null,
                0,
                0,
                0,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                0m,
                Array.Empty<LearnedFactorWeightDto>(),
                "No model has been fitted for this horizon yet. Every pick is priced by the deterministic structural prior.");
        }

        var weights = ToWeightDtos(model);
        var improvement = model.BaselineBrierScore > 0m
            ? decimal.Round(
                (model.BaselineBrierScore - model.TrainedBrierScore) / model.BaselineBrierScore * 100m,
                2,
                MidpointRounding.AwayFromZero)
            : 0m;

        var topFactor = weights.FirstOrDefault();
        var summary = model.IsActive
            ? $"Learned on {model.TrainSampleCount} trades and validated on {model.ValidationSampleCount} held-out trades: " +
              $"Brier {model.TrainedBrierScore:0.000} against a {model.BaselineBrierScore:0.000} neutral prior " +
              $"({improvement:+0.0;-0.0}% better). Live sizing uses a " +
              $"{model.OptimalKellyFractionMultiplier:0.00}x Kelly fraction" +
              (topFactor is null ? "." : $", leaning hardest on {topFactor.Label.ToLowerInvariant()}.")
            : $"Fitted on {model.TrainSampleCount} trades but rejected: the held-out Brier of {model.TrainedBrierScore:0.000} " +
              $"did not beat the {model.BaselineBrierScore:0.000} neutral prior over {model.ValidationSampleCount} " +
              "validation trades. Live pricing stays on the structural prior until a larger sample earns the upgrade.";

        return new TrainedModelSummaryDto(
            horizon,
            model.IsActive ? TrainedModelStatus.Learned : TrainedModelStatus.PriorFallback,
            model.IsActive,
            model.TrainedAtUtc,
            model.TrainStartDate,
            model.TrainEndDate,
            model.ValidationStartDate,
            model.ValidationEndDate,
            model.TrainSampleCount,
            model.ValidationSampleCount,
            model.EmbargoBars,
            model.BaselineBrierScore,
            model.TrainedBrierScore,
            improvement,
            model.BaselineLogLoss,
            model.TrainedLogLoss,
            model.ValidationWinRate,
            model.ValidationExpectancyR,
            model.ValidationProfitFactor,
            model.OptimalKellyFractionMultiplier,
            weights,
            summary);
    }

    /// <summary>
    /// Ranks the weights by the size of one standard deviation's effect. The normalized
    /// impact is the share of total absolute log-odds movement, so the terminal can show
    /// what the model actually leans on instead of a raw coefficient table.
    /// </summary>
    public static IReadOnlyList<LearnedFactorWeightDto> ToWeightDtos(TrainedHorizonModel? model)
    {
        if (model is null)
        {
            return Array.Empty<LearnedFactorWeightDto>();
        }

        var weights = TrainedModelJson.ReadWeights(model.WeightsJson);
        if (weights.Count == 0)
        {
            return Array.Empty<LearnedFactorWeightDto>();
        }

        var totalImpact = weights.Values.Sum(value => Math.Abs(value));

        return weights
            .OrderByDescending(entry => Math.Abs(entry.Value))
            .Select(entry => new LearnedFactorWeightDto(
                entry.Key,
                TrainedModelFeatures.LabelFor(entry.Key),
                decimal.Round((decimal)entry.Value, 4, MidpointRounding.AwayFromZero),
                totalImpact <= 0d
                    ? 0m
                    : decimal.Round(
                        (decimal)(Math.Abs(entry.Value) / totalImpact * 100d),
                        1,
                        MidpointRounding.AwayFromZero),
                entry.Value > 0d ? "SUPPORTS" : entry.Value < 0d ? "DAMPENS" : "NEUTRAL"))
            .ToList();
    }
}
