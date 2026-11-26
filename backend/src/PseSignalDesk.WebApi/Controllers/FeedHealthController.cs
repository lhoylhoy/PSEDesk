using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.WebApi.Controllers;

/// <summary>
/// Read-only diagnostics for the upstreams the desk depends on. It answers the question the run
/// log used to answer alone: which feed answered, which one degraded silently, and which one is
/// switched off by configuration.
/// <para>
/// It carries no prices, no theses and no credentials — only per-feed counters, timestamps and a
/// bounded message — so it is safe to read from a health probe. It stays behind the compliance
/// guard (loopback in <c>PersonalLocal</c>) because feed composition is still operational detail.
/// </para>
/// </summary>
[ApiController]
[Route("api/health")]
public sealed class FeedHealthController : ControllerBase
{
    private readonly IFeedHealthRegistry _feedHealth;
    private readonly ISignalRepository _signals;
    private readonly MarketDataOptions _marketData;
    private readonly OllamaCloudOptions _ollama;

    public FeedHealthController(
        IFeedHealthRegistry feedHealth,
        ISignalRepository signals,
        IOptions<MarketDataOptions> marketData,
        IOptions<OllamaCloudOptions> ollama)
    {
        _feedHealth = feedHealth;
        _signals = signals;
        _marketData = marketData.Value;
        _ollama = ollama.Value;
    }

    /// <summary>
    /// One line per feed plus the stored-session facts, never cached: a health probe must observe
    /// the current state, and a stale "ok" is exactly the failure mode this endpoint exists to
    /// catch.
    /// </summary>
    [HttpGet("feeds")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult<FeedHealthReport>> GetFeeds(CancellationToken cancellationToken = default)
    {
        var feeds = _feedHealth.Snapshot();
        var (available, latestSession, sessionCount, detail) = await ReadStoredFactsAsync(cancellationToken);

        return Ok(new FeedHealthReport(
            DateTimeOffset.UtcNow,
            OverallStatus(feeds),
            available,
            latestSession,
            sessionCount,
            detail,
            new FeedHealthConfiguration(
                _marketData.EnablePseEdgeHistory,
                _marketData.PseDailyQuotationUrlTemplates.Count,
                _ollama.HasApiKey && _ollama.HasEmbeddingModel,
                _ollama.EmbeddingModel ?? string.Empty),
            feeds));
    }

    /// <summary>
    /// The latest stored session and how many sessions are stored. A database that cannot be read
    /// is reported as "facts unavailable" instead of failing the probe: the feed state is still
    /// worth reading when the store is down, and an exception message could disclose a file path.
    /// </summary>
    private async Task<(bool Available, DateOnly? LatestSession, int SessionCount, string? Detail)> ReadStoredFactsAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            var dates = await _signals.GetSnapshotDatesAsync(cancellationToken);

            if (dates.Count == 0)
            {
                return (true, null, 0, "no live session has been stored yet");
            }

            return (true, dates.Max(), dates.Count, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return (false, null, 0, "the signal store could not be read");
        }
    }

    /// <summary>
    /// The worst status across the feeds the desk cannot rank without. The best-effort sources
    /// (macro dials, the operator-supplied DQR, the embeddings endpoint) are reported per feed but
    /// deliberately excluded: an absent EPHE quote must not paint the whole desk red when the tape
    /// is perfectly readable. A feed that was never observed is ignored for the same reason.
    /// </summary>
    private static FeedStatus OverallStatus(IReadOnlyList<FeedHealth> feeds)
    {
        var core = feeds
            .Where(feed => feed.Source is FeedSource.PseEdgeHistory
                or FeedSource.PseEdgeDisclosures
                or FeedSource.YahooCharts)
            .Where(feed => feed.Status != FeedStatus.Unknown)
            .ToList();

        if (core.Count == 0)
        {
            return FeedStatus.Unknown;
        }

        if (core.Any(feed => feed.Status == FeedStatus.Failed))
        {
            return FeedStatus.Failed;
        }

        return core.Any(feed => feed.Status == FeedStatus.Degraded)
            ? FeedStatus.Degraded
            : FeedStatus.Ok;
    }
}
