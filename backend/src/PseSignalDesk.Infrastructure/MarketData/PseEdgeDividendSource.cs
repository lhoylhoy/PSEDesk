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
/// One row of a PSE Edge dividends table, exactly as printed: the security column, the dividend
/// type, the rate cell verbatim and the ex-date. Rates are kept as text because the exchange
/// writes several spellings of the same number, and a parser that drops one of them silently
/// understates a yield.
/// </summary>
internal sealed record EdgeDividendRow(
    string Security,
    string Kind,
    string RateText,
    DateOnly? ExDate,
    string CircularNumber);

/// <summary>
/// PSE Edge's per-company dividends table — the only public source of PSE cash dividends that
/// still answers, and therefore what makes a trailing-twelve-month yield real instead of
/// permanently unreported.
/// <para>
/// The page at <c>companyPage/dividends_and_rights_form.do?cmpy_id=</c> is only a shell; the
/// table is loaded by <c>POST companyPage/dividends_and_rights_list.ax?DividendsOrRights=Dividends</c>
/// with the company's numeric <c>cmpy_id</c> as a form field (JFC, probed 2026-11-28). A parallel
/// <c>DividendsOrRights=Rights</c> call answers the entitlement-ratio table, which is not read
/// here.
/// </para>
/// <para>
/// Three facts about the payload decide the parser, all of them observed live:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>The security column mixes series.</b> JFC prints <c>COMMON</c> at Php2.11 next to
/// <c>JFCPB</c> at Php10.60, and GLO prints <c>GLOBA</c>/<c>GLOBB</c> preferred rows whose rate
/// cell is a sentence ("Fixed annual rate of 6.1179% or Php61.179 per share…"). Summing every
/// row would value the common on a preferred's coupon, so only <c>COMMON</c> is read.
/// </description></item>
/// <item><description>
/// <b>Rates are free text.</b> The same table prints <c>Php2.11</c>, <c>Php 0.11</c>,
/// <c>P0.133/Share</c>, <c>Php0.11733697</c> and <c>N/A</c>, so the amount is read from the
/// leading token and rejected when it is a percentage or a sentence.
/// </description></item>
/// <item><description>
/// <b>Only cash is listed.</b> Across the desk's catalog every row is <c>Type of Dividend =
/// Cash</c>; property dividends print as prose (ABA) and stock dividends are not published on
/// this table at all (MAC's 21% stock dividend is absent). The table therefore cannot adjust a
/// series for a stock dividend, which is why this source reports cash only and never invents a
/// split ratio.
/// </description></item>
/// </list>
/// <para>
/// The table is a current snapshot with no paging and uneven depth (SCC and PXP answer
/// <c>no data.</c>), so a name with no rows is reported as a skip rather than a failure, and an
/// absent yield stays unreported instead of being zero-filled. Every failure is soft: a lookup
/// that cannot answer leaves the ticker without a dividend list, which is exactly the state it
/// was in before this source existed.
/// </para>
/// </summary>
internal sealed class PseEdgeDividendSource
{
    /// <summary>
    /// Dividend tables change a few times a year, so the page is cached far longer than a daily
    /// bar: four horizon runs in one session would otherwise re-download the same unchanged table
    /// four times.
    /// </summary>
    internal const int DefaultCacheMinutes = 360;


    private static readonly Regex TableRowRegex = new(
        "<tr[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TableCellRegex = new(
        "<t[dh][^>]*>(?<cell>.*?)</t[dh]>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>
    /// A peso amount at the start of the cell: <c>Php2.11</c>, <c>Php 0.11</c>,
    /// <c>P0.133/Share</c>, <c>PhP11.758</c>, <c>Php17.83250 per share</c>.
    /// </summary>
    private static readonly Regex CurrencyRateRegex = new(
        @"^(?:PHP|PhP|Php|P|₱)\s*(?<amount>\d[\d,]*(?:\.\d+)?)\s*(?<suffix>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>A cell that is nothing but the number, e.g. <c>0.63</c>.</summary>
    private static readonly Regex BareRateRegex = new(
        @"^(?<amount>\d[\d,]*(?:\.\d+)?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The ex-date cell in the exchange's own spellings: <c>Nov 23, 2026</c>, <c>Jul 2, 2026</c>,
    /// <c>May 04, 2026</c>, and the older <c>11/26/2026</c> table style.
    /// </summary>
    private static readonly string[] ExDateFormats =
    {
        "MMM d, yyyy",
        "MMM dd, yyyy",
        "M/d/yyyy",
        "MM/dd/yyyy"
    };

    private readonly HttpClient _client;
    private readonly MarketDataOptions _options;
    private readonly MarketDataCache _cache;
    private readonly ILogger _logger;
    private readonly IFeedHealthRegistry _feedHealth;

    public PseEdgeDividendSource(
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

    private TimeSpan CacheTimeToLive =>
        TimeSpan.FromMinutes(
            _options.PseEdgeDividendsCacheMinutes > 0
                ? _options.PseEdgeDividendsCacheMinutes
                : DefaultCacheMinutes);

    /// <summary>
    /// The cash dividends the exchange attributes to <paramref name="ticker"/>'s common shares
    /// with an ex-date on or before <paramref name="sessionDate"/>, oldest first and one entry per
    /// ex-date. Empty when the name has never paid, when Edge lists no company page, or when the
    /// feed cannot be reached — the feed ledger separates those three.
    /// </summary>
    public async Task<IReadOnlyList<StockDividendEvent>> FetchCashDividendsAsync(
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

            var html = await _cache.GetOrAddAsync(
                $"edge-dividends:{companyId}",
                CacheTimeToLive,
                () => DownloadDividendsAsync(companyId, symbol),
                cancellationToken);

            var rows = ParseRows(html);

            // One circular is one declaration: re-published or corrected rows are dropped before
            // the per-date totals are summed, so a correction cannot double the yield.
            var byCircular = new Dictionary<string, EdgeDividendRow>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (!IsCommonCashRow(row) || row.ExDate is not DateOnly exDate)
                {
                    continue;
                }

                if (exDate > sessionDate)
                {
                    // A declared but not yet ex dividend is not part of a trailing yield.
                    continue;
                }

                byCircular.TryAdd(row.CircularNumber, row);
            }

            var dividends = byCircular.Values
                .Select(row => new StockDividendEvent(row.ExDate!.Value, ParseRate(row.RateText)!.Value))
                .GroupBy(dividend => dividend.ExDate)
                // Decimal addition is exact, so the exchange's own amount is carried through
                // untouched; only two declarations sharing an ex-date are summed.
                .Select(group => new StockDividendEvent(group.Key, group.Sum(dividend => dividend.AmountPerShare)))
                .OrderBy(dividend => dividend.ExDate)
                .ToList();

            if (dividends.Count == 0)
            {
                // The exchange answered and this name has no cash dividend on record. That is not
                // an outage, and it is not a zero payout either: the trailing yield stays null.
                _feedHealth.RecordSkipped(
                    FeedSource.PseEdgeDividends,
                    $"{symbol}: no common cash dividend on record ({rows.Count} row(s) printed).");
            }
            else
            {
                _feedHealth.RecordSuccess(
                    FeedSource.PseEdgeDividends,
                    $"{symbol}: {dividends.Count} cash ex-date(s) from its own Edge page.",
                    dividends.Count);
            }

            return dividends;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller abort (tab closed, horizon switch) is never a feed failure.
            throw;
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning("PSE Edge dividends unavailable for {Ticker}: {Reason}", symbol, ex.Message);

            _feedHealth.RecordFailure(FeedSource.PseEdgeDividends, $"{symbol}: {ex.Message}");

            return Array.Empty<StockDividendEvent>();
        }
        catch (JsonException)
        {
            _feedHealth.RecordFailure(
                FeedSource.PseEdgeDividends,
                $"{symbol}: PSE Edge returned unreadable JSON for the symbol search.");

            return Array.Empty<StockDividendEvent>();
        }
    }



    /// <summary>
    /// Reads the dividends table into rows. A row is kept only when it has the seven cells the
    /// table prints; the header row, the pager and the single-cell <c>no data.</c> row are all
    /// dropped here rather than being special-cased by every caller.
    /// </summary>
    internal static IReadOnlyList<EdgeDividendRow> ParseRows(string html)
    {
        var rows = new List<EdgeDividendRow>();

        foreach (Match rowMatch in TableRowRegex.Matches(html ?? string.Empty))
        {
            var cells = TableCellRegex.Matches(rowMatch.Groups["row"].Value)
                .Select(cell => CleanText(cell.Groups["cell"].Value))
                .ToList();

            if (cells.Count != 7 || cells[0].Length == 0)
            {
                continue;
            }

            rows.Add(new EdgeDividendRow(
                cells[0],
                cells[1],
                cells[2],
                TryParseExDate(cells[3], out var exDate) ? exDate : null,
                cells[6]));
        }

        return rows;
    }

    /// <summary>
    /// The common-share cash rows the yield is built from. The security cell carries the
    /// exchange's own label, and preferred series print their symbol there (<c>JFCPB</c>,
    /// <c>GLOBA</c>), so the test is an equality on <c>COMMON</c> rather than a prefix match.
    /// </summary>
    internal static bool IsCommonCashRow(EdgeDividendRow row) =>
        string.Equals(row.Security, "COMMON", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(row.Kind, "Cash", StringComparison.OrdinalIgnoreCase) &&
        ParseRate(row.RateText) is decimal rate && rate > 0m;

    /// <summary>
    /// The cash amount per share in a rate cell. Returns null for <c>N/A</c>, for a percentage
    /// ("Fixed annual rate of 6.1179%…") and for a sentence that carries no leading amount, so an
    /// unreadable cell understates nothing.
    /// </summary>
    internal static decimal? ParseRate(string? text)
    {
        var trimmed = CleanText(text ?? string.Empty);

        if (trimmed.Length == 0 ||
            string.Equals(trimmed, "N/A", StringComparison.OrdinalIgnoreCase) ||
            trimmed is "-" or "--" or "—")
        {
            return null;
        }

        var currency = CurrencyRateRegex.Match(trimmed);

        if (currency.Success)
        {
            var suffix = currency.Groups["suffix"].Value.TrimStart();

            // "Php6.1179%" is a coupon rate, not pesos per share.
            if (suffix.StartsWith('%'))
            {
                return null;
            }

            // A long cell with a leading amount is prose unless it names the unit; preferring an
            // unreported amount over a misread one is the point of this guard.
            if (trimmed.Length > 40 && !trimmed.Contains("share", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return ReadAmount(currency.Groups["amount"].Value);
        }

        var bare = BareRateRegex.Match(trimmed);

        return bare.Success ? ReadAmount(bare.Groups["amount"].Value) : null;
    }

    private static decimal? ReadAmount(string digits)
    {
        var normalized = digits.Replace(",", string.Empty, StringComparison.Ordinal);

        return decimal.TryParse(
            normalized,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var amount) && amount > 0m
            ? amount
            : null;
    }

    /// <summary>The ex-date cell. A row the exchange leaves blank is not dated.</summary>
    internal static bool TryParseExDate(string? text, out DateOnly exDate)
    {
        exDate = default;

        var trimmed = CleanText(text ?? string.Empty);

        if (trimmed.Length is 0 or > 24 ||
            !DateTime.TryParseExact(
                trimmed,
                ExDateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return false;
        }

        exDate = DateOnly.FromDateTime(parsed);

        return true;
    }

    private IReadOnlyList<StockDividendEvent> Skipped(string reason)
    {
        _feedHealth.RecordSkipped(FeedSource.PseEdgeDividends, reason);

        return Array.Empty<StockDividendEvent>();
    }


    /// <summary>
    /// The company's <c>cmpy_id</c> from the Edge symbol search, cached under the key the history
    /// and disclosure sources also use, so a run that already resolved the ticker pays nothing.
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
    /// The dividends table: a form POST of the numeric company id to the list endpoint. The page
    /// shell's own form posts exactly these four fields, and the endpoint answers the table body
    /// rather than a document.
    /// </summary>
    private Task<string> DownloadDividendsAsync(string companyId, string ticker) =>
        SendAsync(
            new HttpRequestMessage(
                HttpMethod.Post,
                $"{_options.PseEdgeDividendsUrl}?DividendsOrRights=Dividends")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["cmpy_id"] = companyId,
                    ["keyword"] = string.Empty,
                    ["security_id"] = string.Empty,
                    ["pageNo"] = "1"
                })
            },
            $"the {ticker} dividends");

    /// <summary>
    /// The request itself. Every failure is raised as
    /// <see cref="MarketDataUnavailableException"/> so the cache never stores a non-page; the
    /// caller turns it into a reported, non-fatal verdict.
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

    /// <summary>Strips markup, decodes entities and collapses whitespace.</summary>
    private static string CleanText(string raw)
    {
        var text = Regex.Replace(raw ?? string.Empty, "<.*?>", " ");
        text = WebUtility.HtmlDecode(text);

        return Regex.Replace(text, "\\s+", " ").Trim();
    }
}

