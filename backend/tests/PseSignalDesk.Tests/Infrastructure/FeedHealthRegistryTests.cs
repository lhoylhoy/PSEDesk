using PseSignalDesk.Application.Models;
using PseSignalDesk.Infrastructure.Diagnostics;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The feed ledger is what turns a silent degradation into a fact. PSE Edge answers the
/// disclosure listing with HTTP 200 and a "no records found" page, so nothing throws and the run
/// looks healthy while the trap screen is unenforced; these tests pin the status each combination
/// of answers produces, and the run scoping that keeps the report about the current session.
/// </summary>
public sealed class FeedHealthRegistryTests
{
    [Fact]
    public void A_fresh_registry_reports_every_source_as_unknown()
    {
        var snapshot = new FeedHealthRegistry().Snapshot();

        Assert.Equal(Enum.GetValues<FeedSource>().Length, snapshot.Count);
        Assert.Equal(Enum.GetValues<FeedSource>().OrderBy(source => source), snapshot.Select(feed => feed.Source));
        Assert.All(snapshot, feed => Assert.Equal(FeedStatus.Unknown, feed.Status));
        Assert.All(snapshot, feed => Assert.Null(feed.LastObservedUtc));
    }

    [Fact]
    public void A_success_is_ok_and_a_lone_failure_is_failed()
    {
        var registry = new FeedHealthRegistry();

        registry.RecordSuccess(FeedSource.PseEdgeHistory, "JFC: 283 bar(s).", 283);
        registry.RecordFailure(FeedSource.PseEdgeDisclosures, "HTTP 500.");

        Assert.Equal(FeedStatus.Ok, StatusOf(registry, FeedSource.PseEdgeHistory));
        Assert.Equal(FeedStatus.Failed, StatusOf(registry, FeedSource.PseEdgeDisclosures));

        var history = FeedOf(registry, FeedSource.PseEdgeHistory);
        Assert.Equal(1, history.Successes);
        Assert.Equal(283, history.LastItemCount);
        Assert.NotNull(history.LastSuccessUtc);
        Assert.Null(history.LastFailureUtc);
    }

    [Fact]
    public void A_failure_next_to_a_success_is_degraded_not_failed()
    {
        // The whole point of counting rather than reading the last write: 58 names are fetched
        // concurrently, so whichever one finished last says nothing about the feed.
        var registry = new FeedHealthRegistry();

        registry.RecordFailure(FeedSource.PseEdgeHistory, "VLL: no security row.");
        registry.RecordSuccess(FeedSource.PseEdgeHistory, "JFC: 283 bar(s).", 283);

        Assert.Equal(FeedStatus.Degraded, StatusOf(registry, FeedSource.PseEdgeHistory));
    }

    [Fact]
    public void An_explicit_degraded_observation_is_degraded_even_with_successes()
    {
        // This is the live disclosure shape: the pages fetched fine, and none of the filings could
        // be attributed to a ticker.
        var registry = new FeedHealthRegistry();

        registry.RecordSuccess(FeedSource.PseEdgeDisclosures, "page fetched", 0);
        registry.RecordDegraded(FeedSource.PseEdgeDisclosures, "4 filing(s) read, none attributable.");

        var feed = FeedOf(registry, FeedSource.PseEdgeDisclosures);

        Assert.Equal(FeedStatus.Degraded, feed.Status);
        Assert.Equal(1, feed.DegradedObservations);
        Assert.Contains("none attributable", feed.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void A_feed_that_only_ever_skips_is_degraded_not_ok()
    {
        // A 404 for every symbol means the host answered but produced nothing: calling that "ok"
        // would hide a delisting wave.
        var registry = new FeedHealthRegistry();

        registry.RecordSkipped(FeedSource.YahooCharts, "SM.PS: HTTP 404.");

        Assert.Equal(FeedStatus.Degraded, StatusOf(registry, FeedSource.YahooCharts));
        Assert.Equal(0, FeedOf(registry, FeedSource.YahooCharts).Failures);
    }

    [Fact]
    public void Skips_do_not_degrade_a_feed_that_also_produced_series()
    {
        // Yahoo still serves FX and the macro dials while answering 404 for every PSE equity.
        var registry = new FeedHealthRegistry();

        registry.RecordSkipped(FeedSource.YahooCharts, "SM.PS: HTTP 404.");
        registry.RecordSuccess(FeedSource.YahooCharts, "PHP=X: 300 bar(s).", 300);

        Assert.Equal(FeedStatus.Ok, StatusOf(registry, FeedSource.YahooCharts));
        Assert.Equal(1, FeedOf(registry, FeedSource.YahooCharts).Skips);
    }

    [Fact]
    public void BeginRun_clears_the_run_scoped_counters_and_keeps_the_process_lifetime_ones()
    {
        var registry = new FeedHealthRegistry();

        registry.RecordFailure(FeedSource.Embeddings, "HTTP 401.");
        registry.RecordFailure(FeedSource.PseEdgeHistory, "HTTP 503.");

        registry.BeginRun();

        // The tape counters describe the session being fetched, so they start blank...
        Assert.Equal(FeedStatus.Unknown, StatusOf(registry, FeedSource.PseEdgeHistory));

        // ...while the embeddings endpoint is exercised at indexing time, and forgetting the 401
        // would erase the only explanation for a lexical-only retrieval.
        Assert.Equal(FeedStatus.Failed, StatusOf(registry, FeedSource.Embeddings));
    }

    [Fact]
    public void The_last_success_timestamp_survives_a_new_run()
    {
        var registry = new FeedHealthRegistry();

        registry.RecordSuccess(FeedSource.PseEdgeHistory, "JFC: 283 bar(s).", 283);
        var succeededAt = FeedOf(registry, FeedSource.PseEdgeHistory).LastSuccessUtc;

        registry.BeginRun();

        var feed = FeedOf(registry, FeedSource.PseEdgeHistory);

        Assert.NotNull(succeededAt);
        Assert.Equal(succeededAt, feed.LastSuccessUtc);
        Assert.Null(feed.LastDetail);
        Assert.Equal(0, feed.Successes);
    }

    [Fact]
    public void Concurrent_records_are_counted_exactly()
    {
        // The provider runs 8 downloads at a time and every one of them records, so a lost update
        // would quietly under-report a feed that is failing.
        var registry = new FeedHealthRegistry();

        Parallel.For(0, 500, index =>
        {
            if (index % 5 == 0)
            {
                registry.RecordFailure(FeedSource.PseEdgeHistory, $"symbol {index}: HTTP 500.");
            }
            else
            {
                registry.RecordSuccess(FeedSource.PseEdgeHistory, $"symbol {index}: 283 bar(s).", 283);
            }
        });

        var feed = FeedOf(registry, FeedSource.PseEdgeHistory);

        Assert.Equal(400, feed.Successes);
        Assert.Equal(100, feed.Failures);
        Assert.Equal(FeedStatus.Degraded, feed.Status);
    }

    [Fact]
    public void A_long_upstream_message_is_truncated_for_the_response()
    {
        var registry = new FeedHealthRegistry();

        registry.RecordFailure(FeedSource.PseEdgeDisclosures, new string('x', 500));

        var detail = FeedOf(registry, FeedSource.PseEdgeDisclosures).LastDetail;

        Assert.NotNull(detail);
        Assert.Equal(243, detail!.Length);
        Assert.EndsWith("...", detail, StringComparison.Ordinal);
    }

    private static FeedHealth FeedOf(FeedHealthRegistry registry, FeedSource source) =>
        registry.Snapshot().Single(feed => feed.Source == source);

    private static FeedStatus StatusOf(FeedHealthRegistry registry, FeedSource source) =>
        FeedOf(registry, source).Status;
}
