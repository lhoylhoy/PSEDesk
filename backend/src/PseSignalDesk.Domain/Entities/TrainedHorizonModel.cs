using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// One purged walk-forward logistic model per horizon. The desk fits the factor weights on
/// the in-sample slice of its own real ATR-barrier history, grades them on a held-out slice
/// it never saw, and only lets the live edge engine use them when the out-of-sample Brier
/// score actually beats the neutral baseline. When the gate fails the row is still stored —
/// with <see cref="IsActive"/> false — so the terminal can explain why the prior is still in
/// charge instead of pretending a model was learned.
/// </summary>
public class TrainedHorizonModel
{
    public int Id { get; set; }

    /// <summary>Book this model belongs to. One active row per horizon.</summary>
    public SignalHorizon Horizon { get; set; }

    public DateTime TrainedAtUtc { get; set; }

    /// <summary>First entry session in the training slice.</summary>
    public DateOnly TrainStartDate { get; set; }

    /// <summary>Last entry session whose outcome had fully resolved before the purge cut.</summary>
    public DateOnly TrainEndDate { get; set; }

    /// <summary>First held-out entry session. Never overlaps the training slice.</summary>
    public DateOnly ValidationStartDate { get; set; }

    public DateOnly ValidationEndDate { get; set; }

    public int TrainSampleCount { get; set; }

    public int ValidationSampleCount { get; set; }

    /// <summary>
    /// Gap, in sessions, between the last training outcome and the first validation entry.
    /// Labelled trades overlap in time, so without an embargo the held-out slice would leak
    /// information from trades whose forward windows had not closed yet.
    /// </summary>
    public int EmbargoBars { get; set; }

    /// <summary>Brier score of the neutral prior on the held-out slice. Lower is better.</summary>
    public decimal BaselineBrierScore { get; set; }

    /// <summary>Brier score of the learned weights on the held-out slice. Lower is better.</summary>
    public decimal TrainedBrierScore { get; set; }

    public decimal BaselineLogLoss { get; set; }

    public decimal TrainedLogLoss { get; set; }

    public decimal ValidationWinRate { get; set; }

    /// <summary>Mean fee-paid realized R per held-out trade.</summary>
    public decimal ValidationExpectancyR { get; set; }

    public decimal ValidationProfitFactor { get; set; }

    /// <summary>
    /// Learned fraction of full Kelly that maximized held-out log growth within the
    /// drawdown budget, in <c>[0.15, 0.35]</c>. The live edge engine rescales the
    /// quarter-Kelly stake with it.
    /// </summary>
    public decimal OptimalKellyFractionMultiplier { get; set; }

    /// <summary>Logistic intercept of the fitted model (log-odds space).</summary>
    public decimal Intercept { get; set; }

    /// <summary>JSON object of feature key → learned weight, in log-odds space.</summary>
    public string WeightsJson { get; set; } = "{}";

    /// <summary>JSON object of feature key → { mean, scale } fitted on the training slice only.</summary>
    public string FeatureStatsJson { get; set; } = "{}";

    /// <summary>
    /// True only when the held-out sample was large enough and the learned Brier score beat
    /// the neutral baseline. An inactive model is documentation, not an input to live sizing.
    /// </summary>
    public bool IsActive { get; set; }
}
