using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;

namespace PseSignalDesk.Infrastructure.Diagnostics;

/// <summary>
/// Process-lifetime, in-memory feed ledger. It is deliberately not persisted: a restart loses the
/// ledger and reports <see cref="FeedStatus.Unknown"/> until the first fetch, which is the honest
/// answer — the desk has not read anything yet, and an invented "ok" would be worse than a blank.
/// <para>
/// Status is derived from counters rather than from the last write, because the provider fetches
/// up to 58 names concurrently and the order in which they finish is not meaningful. "Twelve
/// names skipped, four hundred answered" is <see cref="FeedStatus.Degraded"/> no matter which
/// download happened to land last.
/// </para>
/// </summary>
public sealed class FeedHealthRegistry : IFeedHealthRegistry
{
    /// <summary>
    /// Sources whose counters describe one pipeline run. The embeddings endpoint is excluded: it
    /// is exercised at startup indexing and on upload, so resetting it on a run would erase the
    /// one fact that explains why retrieval has no vectors.
    /// </summary>
    private static readonly FeedSource[] RunScopedSources =
    {
        FeedSource.PseEdgeHistory,
        FeedSource.PseEdgeDisclosures,
        FeedSource.PseEdgeDividends,
        FeedSource.YahooCharts,
        FeedSource.MacroDials,
        FeedSource.QuotationReport
    };

    private static readonly FeedSource[] AllSources = Enum.GetValues<FeedSource>();

    /// <summary>Upstream messages are quoted into a JSON response, so they are bounded.</summary>
    private const int MaximumDetailLength = 240;

    private readonly object _gate = new();
    private readonly Dictionary<FeedSource, Tally> _tallies = AllSources.ToDictionary(source => source, _ => new Tally());

    public void BeginRun()
    {
        lock (_gate)
        {
            foreach (var source in RunScopedSources)
            {
                var tally = _tallies[source];
                tally.Successes = 0;
                tally.Failures = 0;
                tally.Skips = 0;
                tally.Degraded = 0;
                tally.LastItemCount = 0;
                tally.LastDetail = null;
            }
        }
    }

    public void RecordSuccess(FeedSource source, string detail, int itemCount = 0) =>
        Record(source, tally => tally.Successes++, detail, itemCount, failure: false);

    public void RecordDegraded(FeedSource source, string detail, int itemCount = 0) =>
        Record(source, tally => tally.Degraded++, detail, itemCount, failure: false);

    public void RecordFailure(FeedSource source, string detail) =>
        Record(source, tally => tally.Failures++, detail, itemCount: 0, failure: true);

    public void RecordSkipped(FeedSource source, string detail) =>
        Record(source, tally => tally.Skips++, detail, itemCount: 0, failure: false);

    public IReadOnlyList<FeedHealth> Snapshot()
    {
        lock (_gate)
        {
            return AllSources
                .Select(source =>
                {
                    var tally = _tallies[source];

                    return new FeedHealth(
                        source,
                        Derive(tally),
                        tally.Successes,
                        tally.Failures,
                        tally.Skips,
                        tally.Degraded,
                        tally.LastItemCount,
                        tally.LastSuccessUtc,
                        tally.LastFailureUtc,
                        tally.LastObservedUtc,
                        tally.LastDetail);
                })
                .ToList();
        }
    }

    private void Record(
        FeedSource source,
        Action<Tally> count,
        string detail,
        int itemCount,
        bool failure)
    {
        var observedAt = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            var tally = _tallies[source];

            count(tally);

            tally.LastObservedUtc = observedAt;
            tally.LastItemCount = itemCount;
            tally.LastDetail = Truncate(detail);

            if (failure)
            {
                tally.LastFailureUtc = observedAt;
            }
            else
            {
                tally.LastSuccessUtc = observedAt;
            }
        }
    }

    /// <summary>
    /// The rules, in order: nothing observed is unknown; nothing but failures is failed; any
    /// failure next to a success (or an explicit degraded observation) is degraded; a success
    /// alone is ok; and a feed that has only ever skipped names is degraded, because it has not
    /// actually produced anything yet.
    /// </summary>
    private static FeedStatus Derive(Tally tally)
    {
        if (tally.Successes == 0 && tally.Failures == 0 && tally.Skips == 0 && tally.Degraded == 0)
        {
            return FeedStatus.Unknown;
        }

        if (tally.Failures > 0 && tally.Successes == 0)
        {
            return FeedStatus.Failed;
        }

        if (tally.Failures > 0 || tally.Degraded > 0)
        {
            return FeedStatus.Degraded;
        }

        return tally.Successes > 0 ? FeedStatus.Ok : FeedStatus.Degraded;
    }

    private static string? Truncate(string? detail) =>
        string.IsNullOrWhiteSpace(detail)
            ? null
            : detail.Length <= MaximumDetailLength ? detail : detail[..MaximumDetailLength] + "...";

    private sealed class Tally
    {
        public int Successes { get; set; }

        public int Failures { get; set; }

        public int Skips { get; set; }

        public int Degraded { get; set; }

        public int LastItemCount { get; set; }

        public string? LastDetail { get; set; }

        public DateTimeOffset? LastSuccessUtc { get; set; }

        public DateTimeOffset? LastFailureUtc { get; set; }

        public DateTimeOffset? LastObservedUtc { get; set; }
    }
}
