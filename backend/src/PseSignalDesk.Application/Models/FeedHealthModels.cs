namespace PseSignalDesk.Application.Models;

/// <summary>
/// One upstream the desk reads. The enum is the public name of the feed, so it is serialised as
/// a string like every other enum in the API contract.
/// </summary>
public enum FeedSource
{
    /// <summary>The exchange's own daily bars (<c>edge.pse.com.ph</c>). Load-bearing.</summary>
    PseEdgeHistory,

    /// <summary>PSE Edge announcements and company disclosures. Load-bearing for trap flags.</summary>
    PseEdgeDisclosures,

    /// <summary>Yahoo Finance v8 charts. Today only the FX series is expected to answer.</summary>
    YahooCharts,

    /// <summary>The three cross-asset dials (US 10Y, Brent, EPHE). Best-effort.</summary>
    MacroDials,

    /// <summary>The official PSE Daily Quotation Report. Operator-configured, optional.</summary>
    QuotationReport,

    /// <summary>The Ollama embeddings endpoint used by retrieval. Optional.</summary>
    Embeddings
}

/// <summary>
/// How a feed behaved. <see cref="Unknown"/> means the process has not observed it since it
/// started (or since the last run began) — which is different from a feed that answered badly.
/// </summary>
public enum FeedStatus
{
    Unknown,
    Ok,
    Degraded,
    Failed
}

/// <summary>
/// What one upstream did during the last run. Counters are run-scoped (they reset when a run
/// begins) so the report describes the session an operator is asking about; the two timestamps
/// are process-wide so staleness survives a quiet day with no run.
/// </summary>
public sealed record FeedHealth(
    FeedSource Source,
    FeedStatus Status,
    int Successes,
    int Failures,
    int Skips,
    int DegradedObservations,
    int LastItemCount,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    DateTimeOffset? LastObservedUtc,
    string? LastDetail);

/// <summary>
/// The operator's answer to "is the desk actually reading the market?" — one line per upstream
/// plus the facts the app already persisted. Nothing here is a price or an opinion.
/// </summary>
public sealed record FeedHealthReport(
    DateTimeOffset ObservedUtc,
    FeedStatus OverallStatus,
    bool StoredFactsAvailable,
    DateOnly? LatestStoredSessionDate,
    int StoredSessionCount,
    string? StoredFactsDetail,
    FeedHealthConfiguration Configuration,
    IReadOnlyList<FeedHealth> Feeds);

/// <summary>
/// The configuration facts that decide whether an <see cref="FeedStatus.Unknown"/> is expected
/// (a feed that is switched off) or a problem (a feed that should have answered).
/// </summary>
public sealed record FeedHealthConfiguration(
    bool PseEdgeHistoryEnabled,
    int QuotationReportTemplates,
    bool EmbeddingsConfigured,
    string EmbeddingModel);
