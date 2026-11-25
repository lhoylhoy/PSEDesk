using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// A single generated signal run (one per trading day and horizon).
/// </summary>
public class SignalRun
{
    public int Id { get; set; }

    public DateOnly RunDate { get; set; }

    public SignalHorizon Horizon { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Raw model response (already parsed/validated) kept for audit and debugging.
    /// </summary>
    public string RawModelResponseJson { get; set; } = string.Empty;

    public List<SignalItem> Items { get; set; } = new();

    public List<SignalDrop> Drops { get; set; } = new();

    /// <summary>
    /// Recommended portfolio cash reserve for this run, in percent. Set by the pipeline
    /// from the tape character, breadth and how many setups actually cleared the
    /// expected-value gate. Zero on runs persisted before the field existed.
    /// </summary>
    public decimal RecommendedCashPct { get; set; }

    /// <summary>
    /// Share of the evaluated universe above its 50D average on the run date. Null when
    /// the run predates breadth tracking.
    /// </summary>
    public decimal? BreadthAboveSma50Pct { get; set; }

    /// <summary>
    /// Share of the evaluated universe above its 200D average on the run date.
    /// </summary>
    public decimal? BreadthAboveSma200Pct { get; set; }

    /// <summary>
    /// Number of names in a Weinstein Stage 2 uptrend on the run date.
    /// </summary>
    public int? Stage2Count { get; set; }

    /// <summary>
    /// Number of names the screen evaluated on the run date.
    /// </summary>
    public int? EvaluatedStockCount { get; set; }
}
