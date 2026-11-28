using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The closed loop only stays honest if two things hold: an unpromoted fit can never reach the
/// live path, and a promoted one may move the odds by a bounded amount — never enough to
/// override the structural prior. These tests pin both properties.
/// </summary>
public sealed class LearnedEdgeModelTests
{
    private const string TiltKey = "relative_strength_63d";

    [Fact]
    public void FromActive_refuses_a_fit_that_never_earned_its_promotion()
    {
        Assert.Null(LearnedEdgeModel.FromActive(BuildModel(isActive: false)));
        Assert.Null(LearnedEdgeModel.FromActive(null));
    }

    [Fact]
    public void FromActive_refuses_a_promoted_model_with_unreadable_weights()
    {
        Assert.Null(LearnedEdgeModel.FromActive(
            BuildModel(isActive: true, weightsJson: "{}", statsJson: "{}")));
    }

    [Fact]
    public void Tilt_halves_the_deviation_and_never_moves_more_than_six_points()
    {
        var model = LearnedEdgeModel.FromActive(BuildModel(isActive: true))!;
        var features = NeutralFeatures();

        Assert.Equal(0.5m, model.BaseProbability);

        // eta = 1.0 * z with a unit scale, so a +1 sigma candidate sits at sigmoid(1) = 0.7311:
        // half of the 0.2311 deviation is 0.1155, which the six-point cap must truncate.
        features[0] = 1m;
        Assert.Equal(LearnedEdgeModel.MaximumTilt, model.Tilt(features));

        features[0] = -1m;
        Assert.Equal(-LearnedEdgeModel.MaximumTilt, model.Tilt(features));

        // A candidate exactly at the training mean has no deviation at all.
        features[0] = 0m;
        Assert.Equal(0m, model.Tilt(features));
    }

    [Fact]
    public void Tilt_mean_imputes_a_missing_factor_instead_of_inventing_an_edge()
    {
        var model = LearnedEdgeModel.FromActive(BuildModel(isActive: true))!;
        var features = NeutralFeatures();
        features[0] = null;

        Assert.Equal(0m, model.Tilt(features));
    }

    [Fact]
    public void KellyScale_rebases_the_stake_against_the_neutral_quarter_kelly()
    {
        var neutral = LearnedEdgeModel.FromActive(
            BuildModel(isActive: true, kellyFraction: LearnedEdgeModel.NeutralKellyMultiplier))!;
        var conservative = LearnedEdgeModel.FromActive(BuildModel(isActive: true, kellyFraction: 0.15m))!;
        var missing = LearnedEdgeModel.FromActive(BuildModel(isActive: true, kellyFraction: 0m))!;

        Assert.Equal(1.0m, neutral.KellyScale);
        Assert.Equal(0.6m, conservative.KellyScale);
        Assert.Equal(LearnedEdgeModel.NeutralKellyMultiplier, missing.KellyFractionMultiplier);
        Assert.Equal(1.0m, missing.KellyScale);
    }

    [Fact]
    public void EvaluateSetup_applies_the_learned_tilt_and_rescales_kelly()
    {
        var service = new ProbabilisticEdgeService();
        var model = LearnedEdgeModel.FromActive(BuildModel(isActive: true, kellyFraction: 0.15m))!;
        var features = NeutralFeatures();
        features[0] = 1m;

        var baseline = service.EvaluateSetup(StrongRequest());
        var learned = service.EvaluateSetup(StrongRequest() with
        {
            LearnedModel = model,
            LearnedFeatures = features
        });

        Assert.Equal(0m, baseline.LearnedEdgeTilt);
        Assert.Equal(LearnedEdgeModel.MaximumTilt, learned.LearnedEdgeTilt);
        Assert.Equal(baseline.WinProbability + LearnedEdgeModel.MaximumTilt, learned.WinProbability);
        Assert.True(learned.QuarterKellyFraction > 0m);

        // The promoted model also owns the stake: 0.15x full Kelly is 0.6x of Quarter-Kelly,
        // measured at the probability the trade actually gets.
        var expectedStake = PseBoardLotService.ComputeQuarterKellyFraction(
            learned.WinProbability,
            learned.NetRiskReward);

        Assert.Equal(
            decimal.Round(expectedStake * 0.6m, 6, MidpointRounding.AwayFromZero),
            learned.QuarterKellyFraction);
    }

    [Fact]
    public void EvaluateSetup_keeps_the_prior_when_the_feature_vector_is_absent()
    {
        var service = new ProbabilisticEdgeService();
        var model = LearnedEdgeModel.FromActive(BuildModel(isActive: true))!;

        var baseline = service.EvaluateSetup(StrongRequest());
        var learned = service.EvaluateSetup(StrongRequest() with { LearnedModel = model });

        Assert.Equal(baseline.WinProbability, learned.WinProbability);
        Assert.Equal(0m, learned.LearnedEdgeTilt);
    }
    [Fact]
    public void TrainedModelFeatures_append_the_entry_and_income_columns_without_reordering_the_first_twelve()
    {
        // A stored fit addresses its weights by key, so the twelve original columns have to keep
        // both their order and their names; only the tail may grow.
        Assert.Equal(14, TrainedModelFeatures.Length);
        Assert.Equal(TrainedModelFeatures.Length, TrainedModelFeatures.Keys.Length);
        Assert.Equal(TrainedModelFeatures.Keys.Length, TrainedModelFeatures.Labels.Length);
        Assert.Equal("relative_strength_63d", TrainedModelFeatures.Keys[0]);
        Assert.Equal("log10_turnover_20d", TrainedModelFeatures.Keys[11]);
        Assert.Equal("dist_from_ema20_atr", TrainedModelFeatures.Keys[12]);
        Assert.Equal("trailing_dividend_yield_pct", TrainedModelFeatures.Keys[13]);
    }

    [Fact]
    public void TrainedModelFeatures_reads_the_entry_distance_and_leaves_an_unreported_yield_null()
    {
        var metric = new StockDailyMetric
        {
            ClosePrice = 100m,
            Ema20 = 90m,
            Atr14 = 2m,
            Rsi14 = 55m,
            TrailingDividendYieldPct = 3.5m
        };

        var features = TrainedModelFeatures.FromMetric(metric);

        Assert.Equal(14, features.Length);
        Assert.Equal(5m, features[12]);
        Assert.Equal(3.5m, features[13]);

        // Unreported stays null: a payer the feed did not see is not a 0% payer.
        metric.TrailingDividendYieldPct = null;
        Assert.Null(TrainedModelFeatures.FromMetric(metric)[13]);

        // A zero daily range cannot express a distance, so it is not reported as 0 ATRs.
        metric.Atr14 = 0m;
        Assert.Null(TrainedModelFeatures.FromMetric(metric)[12]);
    }

    [Fact]
    public void ResidualTilt_still_prices_a_fit_stored_before_the_new_columns_existed()
    {
        // A promoted model trained on the original twelve keys has no weight for the appended two.
        // The engine must skip them (mean imputation) rather than misalign the older weights.
        var legacyWeights = TrainedModelFeatures.Keys.Take(12).ToDictionary(
            key => key,
            key => key == TiltKey ? 1.0d : 0.0d,
            StringComparer.Ordinal);
        var legacyStats = TrainedModelFeatures.Keys.Take(12).ToDictionary(
            key => key,
            _ => new FeatureStat(0d, 1d),
            StringComparer.Ordinal);

        var legacy = LearnedEdgeModel.FromActive(BuildModel(
            isActive: true,
            weightsJson: TrainedModelJson.WriteWeights(legacyWeights),
            statsJson: TrainedModelJson.WriteStats(legacyStats)))!;
        var current = LearnedEdgeModel.FromActive(BuildModel(isActive: true))!;

        var features = NeutralFeatures();
        features[0] = 0.4m;
        features[12] = 9m;
        features[13] = 40m;

        Assert.Equal(current.ResidualTilt(0.5m, features), legacy.ResidualTilt(0.5m, features));
    }



    private static SetupEdgeRequest StrongRequest() => new(
        EntryPrice: 100m,
        StopLoss: 95m,
        TargetPrice: 115m,
        ConvictionScore: 80m,
        MarketRegime: MarketRegime.BULLISH,
        Profile: HorizonProfile.For(SignalHorizon.WEEK),
        EmpiricalWinRate: 0.50m,
        RelativeStrength63d: 3.4m,
        VolatilityContractionRatio: 0.78m,
        WeinsteinStage: "STAGE_2",
        BreadthAboveSma50Pct: 66m);

    /// <summary>All fourteen factors at their training mean, so only the tilted one can move.</summary>
    private static decimal?[] NeutralFeatures()
    {
        var features = new decimal?[TrainedModelFeatures.Length];

        for (var index = 0; index < features.Length; index++)
        {
            features[index] = 0m;
        }

        return features;
    }

    private static TrainedHorizonModel BuildModel(
        bool isActive,
        string? weightsJson = null,
        string? statsJson = null,
        decimal kellyFraction = 0.25m)
    {
        var weights = TrainedModelFeatures.Keys.ToDictionary(
            key => key,
            key => key == TiltKey ? 1.0d : 0.0d,
            StringComparer.Ordinal);
        var stats = TrainedModelFeatures.Keys.ToDictionary(
            key => key,
            _ => new FeatureStat(0d, 1d),
            StringComparer.Ordinal);

        return new TrainedHorizonModel
        {
            Horizon = SignalHorizon.WEEK,
            TrainedAtUtc = new DateTime(2026, 11, 25, 0, 0, 0, DateTimeKind.Utc),
            TrainStartDate = new DateOnly(2023, 1, 2),
            TrainEndDate = new DateOnly(2026, 6, 30),
            ValidationStartDate = new DateOnly(2026, 7, 1),
            ValidationEndDate = new DateOnly(2026, 11, 24),
            TrainSampleCount = 120,
            ValidationSampleCount = 60,
            EmbargoBars = 10,
            Intercept = 0m,
            WeightsJson = weightsJson ?? TrainedModelJson.WriteWeights(weights),
            FeatureStatsJson = statsJson ?? TrainedModelJson.WriteStats(stats),
            BaselineBrierScore = 0.24m,
            TrainedBrierScore = 0.22m,
            BaselineLogLoss = 0.68m,
            TrainedLogLoss = 0.64m,
            ValidationWinRate = 0.50m,
            ValidationExpectancyR = 0.18m,
            ValidationProfitFactor = 1.6m,
            OptimalKellyFractionMultiplier = kellyFraction,
            IsActive = isActive
        };
    }
}
