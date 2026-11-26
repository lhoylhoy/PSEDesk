using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Diagnostics;

namespace PseSignalDesk.Infrastructure.MarketData;

/// <summary>
/// One row of a PSE Edge disclosure table: what was filed, the session it belongs to, and the
/// exact time it hit the wire when the exchange publishes one.
/// </summary>
internal sealed record EdgeFiling(DateOnly Date, string Headline, TimeOnly? FiledAtPht);

/// <summary>
/// Resolves a ticker to the exchange's own <c>cmpy_id</c> through the Edge symbol search. Both
/// Edge sources (daily history and disclosures) start from the same 82-byte answer, so the
/// reader lives here instead of being copied per caller.
/// </summary>
internal static class EdgeSymbolSearch
{
    /// <summary>
    /// The Edge symbol search answer for one ticker, matched on the exchange symbol. The payload
    /// is <c>[{ "cmpyId": "86", "cmpyNm": "Jollibee Foods Corporation", "symbol": "JFC" }]</c>.
    /// </summary>
    internal static string? ReadCompanyId(string json, string ticker)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("symbol", out var symbolElement) ||
                !string.Equals(symbolElement.GetString(), ticker, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!item.TryGetProperty("cmpyId", out var idElement))
            {
                continue;
            }

            var companyId = idElement.ValueKind == JsonValueKind.Number
                ? idElement.GetRawText()
                : idElement.GetString();

            if (!string.IsNullOrWhiteSpace(companyId))
            {
                return companyId.Trim();
            }
        }

        return null;
    }
}

/// <summary>
/// PSE Edge's per-company disclosure listing: the attribution-correct half of the disclosure
/// feed.
/// <para>
/// The market-wide page (<c>companyDisclosures/search.ax</c> with no query) prints a table of
/// <b>subject, date, PSE form number, report number</b> — no company column at all — so the only
/// way to attach a filing to an issuer is to match its headline against a company name, and a
/// template like "Mergers and Consolidations" or "Daily Trading Information" names nobody. A
/// rights offering could therefore leave the trap screen unenforced without any error.
/// </para>
/// <para>
/// The page's own search form carries a hidden <c>keyword</c> field set to the numeric
/// <c>cmpy_id</c>, and <c>GET companyDisclosures/search.ax?keyword={cmpy_id}</c> answers that
/// company's filing table (JFC, probed 2026-11-26: <c>Total 424</c>, fifty rows per page,
/// <c>Nov 26, 2026 11:53 AM</c>). Attribution is then the exchange's identifier rather than a
/// guess, which is what makes a structural trap flag provable.
/// </para>
/// <para>
/// One page is enough per run: the newest filings sit at the top, and the pipeline keeps a
/// rolling 120-day view across sessions, so page two would re-read what the merge already
/// stored. Every failure is soft — a lookup that fails leaves that name on the market-wide
/// scrape and is reported through the feed ledger, never thrown into a run.
/// </para>
/// </summary>
internal sealed class PseEdgeDisclosureSource
{
    private static readonly TimeSpan CacheTimeToLive = TimeSpan.FromMinutes(10);

    private static readonly Regex TableRowRegex = new(
        "<tr[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TableCellRegex = new(
        "<t[dh][^>]*>(?<cell>.*?)</t[dh]>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>The row's link text is the filing's own subject line.</summary>
    private static readonly Regex AnchorTextRegex = new(
        "<a[^>]*>(?<text>[^<]{1,240})</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Page furniture that is a link but never a filing subject.</summary>
    private static readonly Regex NoiseTextRegex = new(
        @"^(?:view|download|print|share|more|read|details?|back|next|previous)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Edge prints <c>Nov 26, 2026 11:53 AM</c> (and a leading-zero hour, <c>05:20 PM</c>) on the
    /// disclosure table, and <c>11/26/2026</c> on the older announcement table. A bare date has no
    /// time of day, so no filing time is claimed for it.
    /// </summary>
    private static readonly string[] AnnounceDateFormats =
    {
        "MMM d, yyyy h:mm tt",
        "MMM d, yyyy h:mm:ss tt",
        "MMM dd, yyyy h:mm tt",
        "MMM d, yyyy",
        "M/d/yyyy h:mm tt",
        "M/d/yyyy"
    };

    private readonly HttpClient _client;
    private readonly MarketDataOptions _options;
    private readonly MarketDataCache _cache;
    private readonly ILogger _logger;
    private readonly IFeedHealthRegistry _feedHealth;

    public PseEdgeDisclosureSource(
        HttpClient httpClient,
        MarketDataOptions options,
        MarketDataCache cache,
        ILogger logger,
        IFeedHealthRegistry? feedHealth = null)
    {
        _client = httpClient;
        _options = options;
        _cache = cache;
        _logger = logger;
        _feedHealth = feedHealth ?? new FeedHealthRegistry();
    }

    /// <summary>
    /// The filings Edge attributes to <paramref name="ticker"/> itself, newest first, inside the
    /// rolling disclosure window that ends on <paramref name="sessionDate"/>. Empty when the
    /// exchange lists no such company, publishes nothing in the window, or cannot be reached —
    /// the feed ledger separates those three.
    /// </summary>
    public async Task<IReadOnlyList<StockDisclosure>> FetchAsync(
        string ticker,
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        var symbol = (ticker ?? string.Empty).Trim().ToUpperInvariant();

        if (symbol.Length == 0)
        {
            return Skipped("no ticker to resolve");
        }

        try
        {
            var companyId = await ResolveCompanyIdAsync(symbol, cancellationToken);

            if (companyId is null)
            {
                return Skipped($"{symbol}: PSE Edge lists no company page");
            }

            var url = $"{_options.PseEdgeCompanyDisclosuresUrl}?keyword={Uri.EscapeDataString(companyId)}";

            var html = await _cache.GetOrAddAsync(
                $"edge-company-disclosures:{companyId}",
                CacheTimeToLive,
                () => DownloadAsync(url, $"the {symbol} disclosures"),
                cancellationToken);

            var windowStart = sessionDate.AddDays(-QuantitativeFilterService.MaxRollingDisclosureLookbackDays);

            var disclosures = ReadFilings(html)
                .Where(filing => filing.Date >= windowStart && filing.Date <= sessionDate)
                .OrderByDescending(filing => filing.Date)
                .ThenByDescending(filing => filing.FiledAtPht ?? TimeOnly.MinValue)
                .Select(filing => new StockDisclosure(
                    filing.Date,
                    filing.Headline,
                    "Disclosure",
                    "PSE Edge",
                    filing.FiledAtPht))
                .ToList();

            if (disclosures.Count == 0)
            {
                // The exchange answered and this company simply has nothing inside the window.
                // That is not an outage, and it is not the market-wide "no records" state either.
                _feedHealth.RecordSkipped(
                    FeedSource.PseEdgeDisclosures,
                    $"{symbol}: no filing inside the {QuantitativeFilterService.MaxRollingDisclosureLookbackDays}-day window.");
            }
            else
            {
                _feedHealth.RecordSuccess(
                    FeedSource.PseEdgeDisclosures,
                    $"{symbol}: {disclosures.Count} filing(s) from its own Edge page.",
                    disclosures.Count);
            }

            return disclosures;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller abort (tab closed, horizon switch) is never a feed failure.
            throw;
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning("PSE Edge company disclosures unavailable for {Ticker}: {Reason}", symbol, ex.Message);

            _feedHealth.RecordFailure(FeedSource.PseEdgeDisclosures, $"{symbol}: {ex.Message}");

            return Array.Empty<StockDisclosure>();
        }
        catch (JsonException)
        {
            _feedHealth.RecordFailure(
                FeedSource.PseEdgeDisclosures,
                $"{symbol}: PSE Edge returned unreadable JSON for the symbol search.");

            return Array.Empty<StockDisclosure>();
        }
    }


    /// <summary>
    /// The exchange answered and has nothing for this name. That is not a fault, but it is not a
    /// success either, so the ledger keeps the two apart instead of reporting a quiet company as
    /// an outage.
    /// </summary>
    private IReadOnlyList<StockDisclosure> Skipped(string reason)
    {
        _feedHealth.RecordSkipped(FeedSource.PseEdgeDisclosures, reason);

        return Array.Empty<StockDisclosure>();
    }

    /// <summary>
    /// The company's <c>cmpy_id</c> from the Edge symbol search. Static metadata, cached for the
    /// same ten minutes as a page body under the key the history source also uses, so a run that
    /// already resolved the ticker pays nothing for the second lookup.
    /// </summary>
    private async Task<string?> ResolveCompanyIdAsync(string ticker, CancellationToken cancellationToken)
    {
        var searchUrl = $"{_options.PseEdgeSymbolSearchUrl}?term={Uri.EscapeDataString(ticker)}";

        var searchJson = await _cache.GetOrAddAsync(
            $"edge-symbol:{ticker}",
            CacheTimeToLive,
            () => DownloadAsync(searchUrl, $"the {ticker} symbol search"),
            cancellationToken);

        return EdgeSymbolSearch.ReadCompanyId(searchJson, ticker);
    }

    /// <summary>One Edge GET whose body is reused for <see cref="CacheTimeToLive"/>.</summary>
    private Task<string> DownloadAsync(string url, string description) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), description);

    /// <summary>
    /// The GET itself. Every failure is raised as <see cref="MarketDataUnavailableException"/> so
    /// the cache never stores a non-page; the caller turns it into a reported, non-fatal verdict.
    /// </summary>
    private async Task<string> SendAsync(HttpRequestMessage request, string description)
    {
        using (request)
        {
            using var requestTimeout = new CancellationTokenSource();
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

            try
            {
                using var response = await _client.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestTimeout.Token);

                if (!response.IsSuccessStatusCode)
                {
                    throw new MarketDataUnavailableException(
                        $"HTTP {(int)response.StatusCode} from PSE Edge for {description}");
                }

                var body = await response.Content.ReadAsStringAsync(requestTimeout.Token);

                if (string.IsNullOrWhiteSpace(body))
                {
                    throw new MarketDataUnavailableException($"PSE Edge returned an empty body for {description}");
                }

                return body;
            }
            catch (OperationCanceledException ex)
            {
                throw new MarketDataUnavailableException(
                    $"PSE Edge did not answer for {description} within {_options.RequestTimeoutSeconds}s",
                    ex);
            }
            catch (HttpRequestException ex)
            {
                throw new MarketDataUnavailableException($"Could not reach PSE Edge for {description}", ex);
            }
        }
    }


    /// <summary>
    /// Reads a disclosure table into filings. A row is kept only when it links to the document
    /// <b>and</b> states when it was announced: the subject is the link text (the exchange labels
    /// every filing by its template), the date and time are the next cell, and the PSE form and
    /// report numbers that follow are identifiers rather than subjects — reading them as subjects
    /// is what produced three unrelated "filings" per row before.
    /// <para>
    /// A row that cannot be dated is dropped instead of being back-dated to the session: a filing
    /// the source cannot place in time is not evidence of anything, and dating it to today would
    /// claim it was readable before the close.
    /// </para>
    /// </summary>
    private static IReadOnlyList<EdgeFiling> ReadFilings(string html)
    {
        var filings = new List<EdgeFiling>();
        var seen = new HashSet<(DateOnly Date, string Headline)>();

        foreach (Match rowMatch in TableRowRegex.Matches(html))
        {
            var row = rowMatch.Groups["row"].Value;

            if (!row.Contains("<a", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateOnly? date = null;
            TimeOnly? fileTime = null;
            string? headline = null;

            foreach (Match cellMatch in TableCellRegex.Matches(row))
            {
                var cell = cellMatch.Groups["cell"].Value;
                var anchor = AnchorTextRegex.Match(cell);
                var text = CleanText(anchor.Success ? anchor.Groups["text"].Value : cell);

                if (text.Length == 0)
                {
                    continue;
                }

                if (TryParseAnnounceDate(text, out var parsedDate, out var parsedTime))
                {
                    date ??= parsedDate;
                    fileTime ??= parsedTime;
                    continue;
                }

                if (headline is null &&
                    anchor.Success &&
                    text.Length >= 3 &&
                    !NoiseTextRegex.IsMatch(text))
                {
                    headline = text;
                }
            }

            if (headline is null || date is null || !seen.Add((date.Value, headline)))
            {
                continue;
            }

            filings.Add(new EdgeFiling(date.Value, headline, fileTime));
        }

        return filings;
    }

    /// <summary>
    /// The announce cell in either of the exchange's formats. A bare date carries no filing time,
    /// so none is claimed for it: <see cref="StockDisclosure.FiledAtPht"/> stays null rather than
    /// pretending the document was readable at midnight.
    /// </summary>
    private static bool TryParseAnnounceDate(string text, out DateOnly date, out TimeOnly? fileTime)
    {
        date = default;
        fileTime = null;

        var trimmed = text.Trim();

        if (trimmed.Length is 0 or > 32 ||
            !DateTime.TryParseExact(
                trimmed,
                AnnounceDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        date = DateOnly.FromDateTime(parsed);

        if (trimmed.Contains(':', StringComparison.Ordinal))
        {
            fileTime = TimeOnly.FromDateTime(parsed);
        }

        return true;
    }

    /// <summary>Strips markup, decodes entities and collapses whitespace.</summary>
    private static string CleanText(string raw)
    {
        var text = Regex.Replace(raw ?? string.Empty, "<.*?>", " ");
        text = WebUtility.HtmlDecode(text);

        return Regex.Replace(text, "\\s+", " ").Trim();
    }

}
