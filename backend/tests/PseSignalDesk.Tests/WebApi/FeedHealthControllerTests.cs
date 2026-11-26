using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.WebApi.Controllers;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// The feed-health endpoint is the only place an operator can see that a feed answered but produced
/// nothing. These tests pin what the headline status means, that an unreadable store cannot take the
/// endpoint down (or leak its path), and that no credential is ever reflected.
/// </summary>
public sealed class FeedHealthControllerTests
{
    [Fact]
    public async Task GetFeeds_reports_the_worst_load_bearing_status_and_ignores_the_optional_feeds()
    {
        var controller = CreateController(
            new StubFeedHealth(
                Feed(FeedSource.PseEdgeHistory, FeedStatus.Ok),
                Feed(FeedSource.PseEdgeDisclosures, FeedStatus.Degraded),
                Feed(FeedSource.YahooCharts, FeedStatus.Ok),
                Feed(FeedSource.MacroDials, FeedStatus.Failed),
                Feed(FeedSource.QuotationReport, FeedStatus.Unknown),
                Feed(FeedSource.Embeddings, FeedStatus.Unknown)),
            new StubSignalRepository(new DateOnly(2026, 11, 25), new DateOnly(2026, 11, 24)));

        var result = await controller.GetFeeds();

        var report = Assert.IsType<FeedHealthReport>(Assert.IsType<OkObjectResult>(result.Result).Value);

        // A dark EPHE quote must not paint the whole desk red while the tape is readable.
        Assert.Equal(FeedStatus.Degraded, report.OverallStatus);
        Assert.Equal(6, report.Feeds.Count);
        Assert.Equal(new DateOnly(2026, 11, 25), report.LatestStoredSessionDate);
        Assert.Equal(2, report.StoredSessionCount);
        Assert.True(report.StoredFactsAvailable);
        Assert.Null(report.StoredFactsDetail);
    }

    [Fact]
    public async Task GetFeeds_reports_failed_when_the_exchange_history_dies()
    {
        var controller = CreateController(
            new StubFeedHealth(
                Feed(FeedSource.PseEdgeHistory, FeedStatus.Failed),
                Feed(FeedSource.PseEdgeDisclosures, FeedStatus.Ok),
                Feed(FeedSource.YahooCharts, FeedStatus.Ok)),
            new StubSignalRepository());

        var result = await controller.GetFeeds();

        var report = Assert.IsType<FeedHealthReport>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(FeedStatus.Failed, report.OverallStatus);
    }

    [Fact]
    public async Task GetFeeds_reports_unknown_before_the_first_fetch()
    {
        var controller = CreateController(
            new StubFeedHealth(Feed(FeedSource.PseEdgeHistory, FeedStatus.Unknown)),
            new StubSignalRepository());

        var result = await controller.GetFeeds();

        var report = Assert.IsType<FeedHealthReport>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.Equal(FeedStatus.Unknown, report.OverallStatus);
    }

    [Fact]
    public async Task GetFeeds_reports_an_empty_store_honestly()
    {
        var controller = CreateController(
            new StubFeedHealth(Feed(FeedSource.PseEdgeHistory, FeedStatus.Ok)),
            new StubSignalRepository());

        var result = await controller.GetFeeds();

        var report = Assert.IsType<FeedHealthReport>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.True(report.StoredFactsAvailable);
        Assert.Null(report.LatestStoredSessionDate);
        Assert.Equal(0, report.StoredSessionCount);
        Assert.Contains("no live session", report.StoredFactsDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFeeds_stays_up_and_hides_the_path_when_the_signal_store_cannot_be_read()
    {
        var controller = CreateController(
            new StubFeedHealth(Feed(FeedSource.PseEdgeHistory, FeedStatus.Ok)),
            new StubSignalRepository(failure: new InvalidOperationException(
                "SQLite Error 14: unable to open database file /Users/operator/private/pse_signals.db")));

        var result = await controller.GetFeeds();

        var report = Assert.IsType<FeedHealthReport>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.False(report.StoredFactsAvailable);
        Assert.Null(report.LatestStoredSessionDate);
        Assert.Equal("the signal store could not be read", report.StoredFactsDetail);
        Assert.Equal(FeedStatus.Ok, report.OverallStatus);
    }

    [Fact]
    public async Task GetFeeds_reports_configuration_without_ever_reflecting_the_api_key()
    {
        var controller = CreateController(
            new StubFeedHealth(Feed(FeedSource.PseEdgeHistory, FeedStatus.Ok)),
            new StubSignalRepository(),
            marketData: options =>
            {
                options.EnablePseEdgeHistory = false;
                options.PseDailyQuotationUrlTemplates.Add("https://files.example.test/dqr-{yyyy}{MMddyyyy}.pdf");
            },
            ollama: options => options.ApiKey = "do-not-leak-this");

        var result = await controller.GetFeeds();

        var report = Assert.IsType<FeedHealthReport>(Assert.IsType<OkObjectResult>(result.Result).Value);

        Assert.False(report.Configuration.PseEdgeHistoryEnabled);
        Assert.Equal(1, report.Configuration.QuotationReportTemplates);
        Assert.True(report.Configuration.EmbeddingsConfigured);
        Assert.Equal("nomic-embed-text", report.Configuration.EmbeddingModel);

        var serialized = JsonSerializer.Serialize(report);
        Assert.DoesNotContain("do-not-leak-this", serialized, StringComparison.Ordinal);
    }

    private static FeedHealthController CreateController(
        IFeedHealthRegistry feedHealth,
        ISignalRepository signals,
        Action<MarketDataOptions>? marketData = null,
        Action<OllamaCloudOptions>? ollama = null)
    {
        var marketOptions = new MarketDataOptions();
        marketData?.Invoke(marketOptions);

        var ollamaOptions = new OllamaCloudOptions();
        ollama?.Invoke(ollamaOptions);

        return new FeedHealthController(
            feedHealth,
            signals,
            Options.Create(marketOptions),
            Options.Create(ollamaOptions));
    }

    private static FeedHealth Feed(FeedSource source, FeedStatus status) =>
        new(source, status, 0, 0, 0, 0, 0, null, null, null, null);

    /// <summary>A registry that answers with a fixed snapshot; recording is a no-op.</summary>
    private sealed class StubFeedHealth : IFeedHealthRegistry
    {
        private readonly IReadOnlyList<FeedHealth> _feeds;

        public StubFeedHealth(params FeedHealth[] feeds)
        {
            _feeds = feeds;
        }

        public void BeginRun()
        {
        }

        public void RecordSuccess(FeedSource source, string detail, int itemCount = 0)
        {
        }

        public void RecordDegraded(FeedSource source, string detail, int itemCount = 0)
        {
        }

        public void RecordFailure(FeedSource source, string detail)
        {
        }

        public void RecordSkipped(FeedSource source, string detail)
        {
        }

        public IReadOnlyList<FeedHealth> Snapshot() => _feeds;
    }

    /// <summary>
    /// Only the stored-session read is exercised by the endpoint; every other member fails loudly so
    /// a future call made from the health path is caught here instead of silently returning empty
    /// data.
    /// </summary>
    private sealed class StubSignalRepository : ISignalRepository
    {
        private readonly IReadOnlyList<DateOnly> _dates;
        private readonly Exception? _failure;

        public StubSignalRepository(params DateOnly[] dates)
        {
            _dates = dates;
        }

        public StubSignalRepository(Exception failure)
        {
            _dates = Array.Empty<DateOnly>();
            _failure = failure;
        }

        public Task<IReadOnlyList<DateOnly>> GetSnapshotDatesAsync(CancellationToken cancellationToken = default) =>
            _failure is null
                ? Task.FromResult(_dates)
                : Task.FromException<IReadOnlyList<DateOnly>>(_failure);

        public Task<DailyMarketSnapshot?> GetSnapshotAsync(DateOnly tradeDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DailyMarketSnapshot?> GetLatestSnapshotAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsAsync(DateOnly tradeDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StockDailyMetric?> GetMetricAsync(DateOnly tradeDate, string ticker, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DateOnly>> GetRunDatesAsync(SignalHorizon horizon, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignalRun?> GetLatestRunAsync(SignalHorizon horizon, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignalRun?> GetRunAsync(DateOnly runDate, SignalHorizon horizon, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SignalRun?> GetPreviousRunAsync(DateOnly beforeDate, SignalHorizon horizon, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SignalRun>> GetRunsForTickerAsync(string ticker, SignalHorizon horizon, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<SignalRun>> GetAllRunsAsync(SignalHorizon? horizon = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StockDailyMetric>> GetMetricsRangeAsync(DateOnly fromDate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task UpdateResolvedOutcomesAsync(IReadOnlyList<SignalItem> items, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveMarketDataAsync(MarketDataBatch batch, IReadOnlyList<StockDailyMetric> metrics, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSignalRunAsync(SignalRun run, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
