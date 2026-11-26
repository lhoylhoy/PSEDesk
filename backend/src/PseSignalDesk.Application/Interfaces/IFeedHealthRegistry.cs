using PseSignalDesk.Application.Models;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// Records what each upstream actually answered, so the desk can report a silent degradation
/// instead of only logging it. The disclosure pillar, for example, fails open: PSE Edge answers
/// <c>200</c> with a "no records found" page, so no exception is raised and the run looks healthy
/// while the trap screen is inert. This registry turns that silence into a fact an operator (and
/// a health probe) can see.
/// <para>
/// Implementations must be thread-safe: the provider fans out up to
/// <c>MarketData:MaxConcurrency</c> downloads and every one of them may record an observation.
/// </para>
/// </summary>
public interface IFeedHealthRegistry
{
    /// <summary>
    /// Starts a new run: clears the run-scoped counters so the snapshot describes the session
    /// being fetched, not every session since the process started. Process-lifetime sources
    /// (the embeddings endpoint, which is used at indexing time rather than per run) keep their
    /// counters.
    /// </summary>
    void BeginRun();

    /// <summary>The feed answered and the answer was usable.</summary>
    void RecordSuccess(FeedSource source, string detail, int itemCount = 0);

    /// <summary>
    /// The feed answered but the answer is materially incomplete — the state that used to be
    /// invisible: an empty disclosure page, a macro dial that stayed unreported, embeddings that
    /// came back empty.
    /// </summary>
    void RecordDegraded(FeedSource source, string detail, int itemCount = 0);

    /// <summary>A transport, HTTP or parse failure. Not the same as "the symbol is not listed".</summary>
    void RecordFailure(FeedSource source, string detail);

    /// <summary>
    /// The feed answered correctly and there was simply nothing for this name (a 404 for a
    /// delisted symbol, an equity Edge no longer prints, a DQR template that is not published
    /// yet). A skip is not a fault, but a feed that only ever skips is not working either.
    /// </summary>
    void RecordSkipped(FeedSource source, string detail);

    /// <summary>Every source in a fixed order, including ones never observed (as unknown).</summary>
    IReadOnlyList<FeedHealth> Snapshot();
}
