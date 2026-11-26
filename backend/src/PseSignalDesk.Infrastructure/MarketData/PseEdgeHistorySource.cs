using System.Globalization;
using System.Text;
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
/// One symbol's PSE Edge history attempt: the real daily bars, or the reason there are none.
/// A failure is never an exception here — the caller composes it into a feed diagnostic next to
/// whatever Yahoo Finance answered.
/// </summary>
internal sealed record EdgeBarFetch(IReadOnlyList<OhlcvBar> Bars, string? Failure)
{
    public static EdgeBarFetch Failed(string reason) => new(Array.Empty<OhlcvBar>(), reason);
}

/// <summary>
/// Daily OHLCV history from the exchange's own PSE Edge service
/// (<c>edge.pse.com.ph</c>), used when the Yahoo Finance chart for a PSE equity carries no
/// series.
/// <para>
/// Yahoo stopped publishing PSE-listed equities: every <c>{TICKER}.PS</c> symbol answers
/// <c>404</c> ("No data found, symbol may be delisted") and <c>PSEI.PS</c> only keeps a legacy
/// quarterly series plus a live level. PSE Edge — the host the desk already scrapes for
/// disclosures — still publishes the exchange's own daily bars per security, so a run can stay
/// live-only without inventing anything.
/// </para>
/// <para>
/// Three calls make one series: the symbol search resolves <c>TICKER</c> to the Edge
/// <c>cmpy_id</c>, the company page resolves the common security's <c>security_id</c>, and the
/// chart endpoint returns <c>{ OPEN, HIGH, LOW, CLOSE, VALUE, CHART_DATE }</c> per session.
/// </para>
/// <para>
/// <b>Share volume is derived, because Edge reports peso value.</b> The payload carries the
/// exact peso value traded but no share count, so the bar's volume is
/// <c>VALUE / typicalPrice</c>, with the typical price <c>(HIGH + LOW + CLOSE) / 3</c>. On a
/// real print (JFC, 2026-11-26) that lands within 0.01% of the exchange's own volume, and it
/// keeps every derived figure honest: <c>Close x Volume</c> reproduces the official turnover the
/// PHP 5M liquidity floor is measured on, and the volume ratios compare one consistently-derived
/// estimate against another. No price, level or session is ever invented.
/// </para>
/// </summary>
internal sealed class PseEdgeHistorySource
{
    private static readonly TimeSpan CacheTimeToLive = TimeSpan.FromMinutes(10);

    /// <summary>The company page's security picker: <c>&lt;option value="158" ...&gt;JFC&lt;/option&gt;</c>.</summary>
    private static readonly Regex SecurityOptionRegex = new(
        "<option\\s+value=\"(?<id>\\d+)\"[^>]*>\\s*(?<symbol>[A-Za-z0-9]+)\\s*</option>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The same page states the selected security in its chart script.</summary>
    private static readonly Regex PageSecurityScriptRegex = new(
        "sendData\\.security_id\\s*=\\s*\"(?<id>\\d+)\"",
        RegexOptions.Compiled);

    /// <summary>Edge prints <c>Nov 26, 2025 00:00:00</c>; the rest are defensive.</summary>
    private static readonly string[] ChartDateFormats =
    {
        "MMM d, yyyy HH:mm:ss",
        "MMM d, yyyy H:mm:ss",
        "MMM d, yyyy",
        "M/d/yyyy HH:mm:ss",
        "M/d/yyyy"
    };

    private readonly HttpClient _httpClient;
    private readonly MarketDataOptions _options;
    private readonly MarketDataCache _cache;
    private readonly ILogger _logger;
    private readonly IFeedHealthRegistry _feedHealth;

    public PseEdgeHistorySource(
        HttpClient httpClient,
        MarketDataOptions options,
        MarketDataCache cache,
        ILogger logger,
        IFeedHealthRegistry? feedHealth = null)
    {
        _httpClient = httpClient;
        _options = options;
        _cache = cache;
        _logger = logger;
        _feedHealth = feedHealth ?? new FeedHealthRegistry();
    }

    /// <summary>
    /// The daily bars Edge publishes for <paramref name="ticker"/> between
    /// <paramref name="tradeDate"/> - <paramref name="lookbackDays"/> and
    /// <paramref name="tradeDate"/>, oldest first, one row per session.
    /// </summary>
    public async Task<EdgeBarFetch> FetchDailyBarsAsync(
        string ticker,
        DateOnly tradeDate,
        int lookbackDays,
        CancellationToken cancellationToken)
    {
        var symbol = (ticker ?? string.Empty).Trim().ToUpperInvariant();

        if (symbol.Length == 0)
        {
            return Skipped("no ticker to resolve");
        }

        try
        {
            var security = await ResolveSecurityAsync(symbol, cancellationToken);

            if (security is null)
            {
                return Skipped($"PSE Edge lists no security for {symbol}");
            }

            var from = tradeDate.AddDays(-Math.Max(1, lookbackDays));
            var key = $"edge-chart:{security.Value.CompanyId}:{security.Value.SecurityId}:" +
                      $"{from:yyyyMMdd}:{tradeDate:yyyyMMdd}";

            var body = await _cache.GetOrAddAsync(
                key,
                CacheTimeToLive,
                () => DownloadChartAsync(security.Value.CompanyId, security.Value.SecurityId, from, tradeDate),
                cancellationToken);

            return ParseChart(symbol, body, tradeDate, from);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller abort (tab closed, horizon switch) is never a feed failure.
            throw;
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning("PSE Edge history unavailable for {Ticker}: {Reason}", symbol, ex.Message);

            return Failed(ex.Message);
        }
        catch (JsonException)
        {
            return Failed($"PSE Edge returned unreadable JSON for {symbol}");
        }
    }

    /// <summary>
    /// The exchange answered and simply has nothing for this name (no security row, stale series).
    /// That is not a fault, but it is not a success either, so the health ledger keeps the two
    /// apart instead of reporting a dead symbol as an outage.
    /// </summary>
    private EdgeBarFetch Skipped(string reason)
    {
        _feedHealth.RecordSkipped(FeedSource.PseEdgeHistory, reason);

        return EdgeBarFetch.Failed(reason);
    }

    /// <summary>A transport, HTTP or parse failure — the exchange is in trouble, not merely empty.</summary>
    private EdgeBarFetch Failed(string reason)
    {
        _feedHealth.RecordFailure(FeedSource.PseEdgeHistory, reason);

        return EdgeBarFetch.Failed(reason);
    }

    /// <summary>
    /// Resolves the Edge identifiers for one ticker: the symbol search gives the company, the
    /// company page gives the common security. Both answers are static metadata, so they are
    /// cached for the same ten minutes as a chart body.
    /// </summary>
    private async Task<(string CompanyId, string SecurityId)?> ResolveSecurityAsync(
        string ticker,
        CancellationToken cancellationToken)
    {
        var searchUrl = $"{_options.PseEdgeSymbolSearchUrl}?term={Uri.EscapeDataString(ticker)}";

        var searchJson = await _cache.GetOrAddAsync(
            $"edge-symbol:{ticker}",
            CacheTimeToLive,
            () => DownloadAsync(searchUrl, $"the {ticker} symbol search"),
            cancellationToken);

        var companyId = ReadCompanyId(searchJson, ticker);

        if (companyId is null)
        {
            return null;
        }

        var pageUrl = $"{_options.PseEdgeCompanyPageUrl}?cmpy_id={companyId}";

        var pageHtml = await _cache.GetOrAddAsync(
            $"edge-company:{companyId}",
            CacheTimeToLive,
            () => DownloadAsync(pageUrl, $"the {ticker} company page"),
            cancellationToken);

        var securityId = ReadSecurityId(pageHtml, ticker);

        return securityId is null ? null : (companyId, securityId);
    }

    /// <summary>The Edge symbol search answer for one ticker, matched on the exchange symbol.</summary>
    private static string? ReadCompanyId(string json, string ticker) =>
        EdgeSymbolSearch.ReadCompanyId(json, ticker);

    /// <summary>
    /// The security id for the ticker on its company page. An exact symbol match wins, because a
    /// company can list preferred series next to the common; otherwise the security Edge
    /// preselects (and names in the page's own chart script) is used.
    /// </summary>
    private static string? ReadSecurityId(string html, string ticker)
    {
        string? preselected = null;

        foreach (Match match in SecurityOptionRegex.Matches(html))
        {
            var id = match.Groups["id"].Value;

            if (string.Equals(match.Groups["symbol"].Value, ticker, StringComparison.OrdinalIgnoreCase))
            {
                return id;
            }

            preselected ??= id;
        }

        var scriptMatch = PageSecurityScriptRegex.Match(html);

        return scriptMatch.Success ? scriptMatch.Groups["id"].Value : preselected;
    }

    /// <summary>One chart POST against the Edge chart endpoint (<c>DisclosureCht.ax</c>).</summary>
    private Task<string> DownloadChartAsync(
        string companyId,
        string securityId,
        DateOnly from,
        DateOnly to) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, _options.PseEdgeChartUrl)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["cmpy_id"] = companyId,
                        ["security_id"] = securityId,
                        ["startDate"] = from.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture),
                        ["endDate"] = to.ToString("MM-dd-yyyy", CultureInfo.InvariantCulture)
                    }),
                    Encoding.UTF8,
                    "application/json")
            },
            $"the {companyId}/{securityId} history");

    /// <summary>One Edge GET whose body is reused for <see cref="CacheTimeToLive"/>.</summary>
    private Task<string> DownloadAsync(string url, string description) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), description);

    private async Task<string> SendAsync(HttpRequestMessage request, string description)
    {
        using (request)
        {
            using var requestTimeout = new CancellationTokenSource();
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

            try
            {
                using var response = await _httpClient.SendAsync(
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
    /// Turns the Edge payload into one bar per session. Duplicate rows (Edge repeats a session)
    /// collapse to the last print, rows outside the requested window are dropped, and a row
    /// without a usable OHLC print is skipped rather than carried forward.
    /// </summary>
    private EdgeBarFetch ParseChart(string ticker, string body, DateOnly tradeDate, DateOnly from)
    {
        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("chartData", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            return Skipped($"PSE Edge returned no price series for {ticker}");
        }

        var byDate = new SortedDictionary<DateOnly, OhlcvBar>();

        foreach (var row in rows.EnumerateArray())
        {
            if (!TryReadBar(row, out var bar) || bar.Date > tradeDate || bar.Date < from)
            {
                continue;
            }

            byDate[bar.Date] = bar;
        }

        if (byDate.Count == 0)
        {
            return Skipped($"PSE Edge published no daily bars for {ticker} through {tradeDate:yyyy-MM-dd}");
        }

        _feedHealth.RecordSuccess(
            FeedSource.PseEdgeHistory,
            $"{ticker}: {byDate.Count} bar(s) through {tradeDate:yyyy-MM-dd}.",
            byDate.Count);

        return new EdgeBarFetch(byDate.Values.ToList(), null);
    }

    private static bool TryReadBar(JsonElement row, out OhlcvBar bar)
    {
        bar = null!;

        if (row.ValueKind != JsonValueKind.Object ||
            !TryReadDecimal(row, "OPEN", out var open) ||
            !TryReadDecimal(row, "HIGH", out var high) ||
            !TryReadDecimal(row, "LOW", out var low) ||
            !TryReadDecimal(row, "CLOSE", out var close) ||
            open <= 0m || high <= 0m || low <= 0m || close <= 0m)
        {
            return false;
        }

        if (!row.TryGetProperty("CHART_DATE", out var dateElement) ||
            dateElement.ValueKind != JsonValueKind.String ||
            !TryReadDate(dateElement.GetString(), out var date))
        {
            return false;
        }

        bar = new OhlcvBar(date, open, high, low, close, ReadVolume(row, high, low, close));

        return true;
    }

    /// <summary>
    /// Share volume from the official peso value: Edge reports what the session was worth, not
    /// how many shares changed hands, so the count is <c>VALUE / typicalPrice</c>.
    /// </summary>
    private static long ReadVolume(JsonElement row, decimal high, decimal low, decimal close)
    {
        if (!TryReadDecimal(row, "VALUE", out var value) || value <= 0m)
        {
            return 0L;
        }

        var typicalPrice = (high + low + close) / 3m;

        return typicalPrice <= 0m
            ? 0L
            : (long)Math.Round(value / typicalPrice, MidpointRounding.AwayFromZero);
    }

    private static bool TryReadDecimal(JsonElement row, string propertyName, out decimal value)
    {
        value = 0m;

        if (!row.TryGetProperty(propertyName, out var element))
        {
            return false;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number => element.TryGetDecimal(out value),
            JsonValueKind.String => decimal.TryParse(
                element.GetString(),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    private static bool TryReadDate(string? text, out DateOnly date)
    {
        date = default;

        if (string.IsNullOrWhiteSpace(text) ||
            !DateTime.TryParseExact(
                text.Trim(),
                ChartDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        date = DateOnly.FromDateTime(parsed);

        return true;
    }
}
