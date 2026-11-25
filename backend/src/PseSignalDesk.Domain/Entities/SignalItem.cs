using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// One ranked pick inside a signal run. Price levels are always stored after
/// PSE board-lot tick rounding and the R:R is recalculated deterministically.
/// </summary>
public class SignalItem
{
    public int Id { get; set; }

    public int SignalRunId { get; set; }

    public SignalRun? SignalRun { get; set; }

    public int Rank { get; set; }

    public string Ticker { get; set; } = string.Empty;

    public DriftStatus DriftStatus { get; set; }

    public int? PreviousRank { get; set; }

    public int ConvictionScore { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal StopLoss { get; set; }

    public decimal TargetPrice { get; set; }

    public decimal RiskRewardRatio { get; set; }

    public string Thesis { get; set; } = string.Empty;

    public string DeviationReason { get; set; } = string.Empty;

    /// <summary>
    /// Shares for the desk's notional position budget, floored to whole board lots. Zero
    /// means the budget cannot buy a single board lot at the entry price.
    /// </summary>
    public int BoardLotShares { get; set; }

    /// <summary>
    /// JSON array of the knowledge-base references that grounded the thesis
    /// (for example <c>["pse_rules#ex-dividend-price-drops","minervini#vcp"]</c>).
    /// </summary>
    public string CitedReferencesJson { get; set; } = "[]";

    /// <summary>
    /// Fee-adjusted reward-to-risk for this plan. Unlike <see cref="RiskRewardRatio"/> it
    /// charges each leg its own rate: 0.295% to buy and 0.395% to sell (0.690% round trip).
    /// </summary>
    public decimal NetRiskRewardRatio { get; set; }

    /// <summary>
    /// Calibrated probability of touching the target before the stop, produced by
    /// <c>ProbabilisticEdgeService</c> from barrier geometry, the ticker's empirical base
    /// rate, the tape, structure and a bounded conviction tilt. Never model-authored.
    /// </summary>
    public decimal WinProbability { get; set; }

    /// <summary>
    /// Fee-adjusted break-even win rate for this plan. A pick is only a bet when
    /// <see cref="WinProbability"/> clears it.
    /// </summary>
    public decimal BreakEvenWinRate { get; set; }

    /// <summary>
    /// Expected value of one unit of risk in R: <c>p * R_net - (1 - p)</c>.
    /// </summary>
    public decimal ExpectedValueR { get; set; }

    /// <summary>
    /// Quarter-Kelly stake as a fraction of equity, clamped defensively. Zero when the
    /// setup has no edge.
    /// </summary>
    public decimal QuarterKellyFraction { get; set; }

    /// <summary>
    /// Server-owned actionability verdict (TRADE / SMALL_SIZE / WATCH_ONLY).
    /// </summary>
    public EdgeVerdict EdgeVerdict { get; set; } = EdgeVerdict.WATCH_ONLY;

    /// <summary>
    /// Mandatory pre-mortem: the primary way this setup fails. Model-authored when
    /// supplied, otherwise a deterministic server fallback.
    /// </summary>
    public string BearCase { get; set; } = string.Empty;

    /// <summary>
    /// Forward-outcome ledger state, graded against the real sessions after
    /// <see cref="SignalRun.RunDate"/>.
    /// </summary>
    public SignalOutcomeStatus OutcomeStatus { get; set; } = SignalOutcomeStatus.OPEN;

    /// <summary>
    /// Session on which the outcome resolved (barrier touch or horizon expiry).
    /// </summary>
    public DateOnly? OutcomeDate { get; set; }

    /// <summary>
    /// Exit price that produced the outcome (stop, target, or expiry close).
    /// </summary>
    public decimal? OutcomeExitPrice { get; set; }

    /// <summary>
    /// Realized result in units of fee-adjusted risk: about -1.0 for a clean stop,
    /// the net R:R for a target hit, and the fee-adjusted expiry return otherwise.
    /// </summary>
    public decimal? RealizedNetR { get; set; }

    /// <summary>
    /// Sessions held before the outcome resolved.
    /// </summary>
    public int? BarsHeld { get; set; }
}
