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

    /// <summary>
    /// Which engine produced the book, e.g. <c>Ollama Cloud (deepseek-v4.1-flash)</c>. Empty on
    /// runs persisted before provenance tracking.
    /// </summary>
    public string EngineName { get; set; } = string.Empty;

    /// <summary>
    /// The exact model name sent to the provider.
    /// </summary>
    public string ModelName { get; set; } = string.Empty;

    /// <summary>
    /// SHA-256 of the system + user prompt, so two runs can be compared only when they saw the
    /// same instructions and the same candidate table.
    /// </summary>
    public string PromptHash { get; set; } = string.Empty;

    /// <summary>Sampling temperature used for this generation.</summary>
    public decimal ModelTemperature { get; set; }

    /// <summary>Sampling seed used, when one was configured.</summary>
    public int? ModelSeed { get; set; }

    /// <summary>Corrective retries spent on this run (0 or 1).</summary>
    public int ModelRetryCount { get; set; }

    /// <summary>Wall-clock milliseconds for the whole generation, including the fast pre-pass.</summary>
    public int ModelLatencyMs { get; set; }

    /// <summary>Prompt tokens reported by the provider.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion tokens reported by the provider.</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>Characters in the system + user prompt, a provider-independent size measure.</summary>
    public int PromptCharacters { get; set; }

    /// <summary>True when the response was schema-constrained rather than plain JSON mode.</summary>
    public bool SchemaConstrained { get; set; }

    /// <summary>True when the optional fast-extraction note was prepended to the prompt.</summary>
    public bool FastExtractionApplied { get; set; }
}
