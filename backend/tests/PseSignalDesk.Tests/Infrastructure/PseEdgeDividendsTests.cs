using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Diagnostics;
using PseSignalDesk.Infrastructure.MarketData;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The exchange's dividends table, read end to end: the ticker is resolved to its Edge company
/// id, the table is fetched, and the cash rows reach <see cref="StockMarketData.Dividends"/>.
/// <para>
/// The markup below is the shape the exchange actually serves — captured from
/// <c>companyPage/dividends_and_rights_list.ax?DividendsOrRights=Dividends</c> on 2026-11-28 for
/// JFC (86), MAC (106), COSCO (50), ABA (174), GLO (69) and SCC (157). Nothing here opens a
/// socket: one handler answers the URLs a single-symbol research fetch uses.
/// </para>
/// </summary>
public sealed class PseEdgeDividendsTests
{
    private static readonly DateOnly Session = new(2026, 11, 28);

    private const int Bars = 220;

    [Fact]
    public async Task Reads_the_common_cash_row_and_ignores_the_preferred_series()
    {
        // JFC prints its common at Php2.11 beside JFCPB at Php10.60. Reading both would value the
        // common on a preferred's coupon, so only the COMMON row may survive.
        var (provider, _, _) = Create(DividendsTable(
            Row("COMMON", "Cash", "Php2.11", "Nov 23, 2026", "C08010-2026"),
            Row("JFCPB", "Cash", "Php10.60125 per share for Series B preferred shares (JFCPB)",
                "Sep 24, 2026", "C06761-2026"),
            Row("JFCPB", "Cash", "Php10.60125 per share for Series B preferred shares (JFCPB)",
                "Jul 01, 2026", "C04480-2026")));

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        var dividend = Assert.Single(stock!.Dividends!);

        Assert.Equal(new DateOnly(2026, 11, 23), dividend.ExDate);
        Assert.Equal(2.11m, dividend.AmountPerShare);
    }

    [Fact]
    public async Task Reads_every_rate_spelling_the_exchange_publishes()
    {
        // The same column prints "Php2.11", "Php 0.11", "P0.133/Share" and "Php0.11733697".
        var (provider, _, _) = Create(DividendsTable(
            Row("COMMON", "Cash", "Php 0.11", "Sep 09, 2026", "C06114-2026"),
            Row("COMMON", "Cash", "P0.133/Share", "Aug 19, 2026", "C02301-2026"),
            Row("COMMON", "Cash", "Php0.11733697", "May 13, 2026", "C03001-2026"),
            Row("COMMON", "Cash", "0.63", "Feb 10, 2026", "C01001-2026")));

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        Assert.Equal(
            new[] { 0.63m, 0.11733697m, 0.133m, 0.11m },
            stock!.Dividends!.Select(dividend => dividend.AmountPerShare));
    }

    [Fact]
    public async Task Skips_the_rows_it_cannot_value_in_pesos()
    {
        // "N/A" (an old circular with no rate) and a property dividend's prose (ABA) are not cash
        // per share; a percentage in the cell is a coupon, not an amount.
        var (provider, _, _) = Create(DividendsTable(
            Row("COMMON", "Cash", "N/A", string.Empty, "C03368-2020"),
            Row("COMMON", "Property",
                "For every one (1) ABACORE share held, a shareholder shall receive 0.0009 PRIDE SHARE",
                "Jun 23, 2022", "C07354-2025"),
            Row("COMMON", "Cash", "Fixed annual rate of 6.1179% or Php61.179 per share semi-annually",
                "Aug 17, 2026", "C05001-2026"),
            Row("COMMON", "Cash", "Php1.25", "Apr 14, 2026", "C02611-2026")));

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        var dividend = Assert.Single(stock!.Dividends!);

        Assert.Equal(new DateOnly(2026, 4, 14), dividend.ExDate);
        Assert.Equal(1.25m, dividend.AmountPerShare);
    }

    [Fact]
    public async Task Does_not_count_a_declared_dividend_that_has_not_gone_ex_yet()
    {
        // A declared ex-date in the future is not part of a trailing yield.
        var (provider, _, _) = Create(DividendsTable(
            Row("COMMON", "Cash", "Php1.00", "Dec 15, 2026", "C09001-2026"),
            Row("COMMON", "Cash", "Php0.50", "Aug 12, 2026", "C06001-2026")));

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        var dividend = Assert.Single(stock!.Dividends!);

        Assert.Equal(new DateOnly(2026, 8, 12), dividend.ExDate);
    }

    [Fact]
    public async Task Counts_one_declaration_once_and_sums_two_declarations_with_the_same_ex_date()
    {
        // A re-published circular is one declaration; a regular and a special dividend that share
        // an ex-date are two, and the price drops by both.
        var (provider, _, _) = Create(DividendsTable(
            Row("COMMON", "Cash", "Php0.40", "Sep 10, 2026", "C06001-2026"),
            Row("COMMON", "Cash", "Php0.40", "Sep 10, 2026", "C06001-2026"),
            Row("COMMON", "Cash", "Php0.10", "Sep 10, 2026", "C06002-2026")));

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        var dividend = Assert.Single(stock!.Dividends!);

        Assert.Equal(0.50m, dividend.AmountPerShare);
    }

    [Fact]
    public async Task Answers_no_dividends_when_the_exchange_prints_no_data()
    {
        // SCC and PXP answer "no data." rather than an empty table. That is a skip, not a failure,
        // and the yield stays unreported instead of becoming zero.
        var (provider, _, feed) = Create(DividendsTable("no data."));

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        Assert.Empty(stock!.Dividends!);

        var ledger = FeedOf(feed);

        Assert.Equal(FeedStatus.Degraded, ledger.Status);
        Assert.Equal(0, ledger.Failures);
        Assert.True(ledger.Skips > 0);
    }

    [Fact]
    public async Task Answers_no_dividends_and_reports_a_failure_when_the_table_is_unreachable()
    {
        var (provider, _, feed) = Create(tableHtml: null);

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        Assert.Empty(stock!.Dividends!);

        var ledger = FeedOf(feed);

        Assert.True(ledger.Failures > 0);
        Assert.Contains("500", ledger.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Makes_no_dividend_request_when_the_feed_is_switched_off()
    {
        var (provider, handler, _) = Create(
            DividendsTable(Row("COMMON", "Cash", "Php2.11", "Nov 23, 2026", "C08010-2026")),
            options => options.EnablePseEdgeDividends = false);

        var stock = await provider.FetchSingleStockAsync("JFC", Session);

        Assert.Empty(stock!.Dividends!);
        Assert.DoesNotContain(handler.Requests, uri =>
            uri.Contains("dividends_and_rights_list.ax", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reuses_the_cached_table_across_calls()
    {
        var (provider, handler, _) = Create(
            DividendsTable(Row("COMMON", "Cash", "Php2.11", "Nov 23, 2026", "C08010-2026")));

        await provider.FetchSingleStockAsync("JFC", Session);
        var afterFirst = handler.Requests.Count(uri =>
            uri.Contains("dividends_and_rights_list.ax", StringComparison.Ordinal));

        await provider.FetchSingleStockAsync("JFC", Session);

        Assert.Equal(1, afterFirst);
        Assert.Equal(
            afterFirst,
            handler.Requests.Count(uri =>
                uri.Contains("dividends_and_rights_list.ax", StringComparison.Ordinal)));
    }


    /// <summary>One dividends-table row in the exchange's own seven-column markup.</summary>
    private static string Row(string security, string kind, string rate, string exDate, string circular) =>
        $"<tr><td>{security}</td><td>{kind}</td><td>{rate}</td><td>{exDate}</td>" +
        "<td>N/A</td><td>N/A</td>" +
        $"<td>{circular}</td></tr>";

    private static string DividendsTable(params string[] rows) =>
        "<table class=\"list\"><thead><tr>" +
        "<th>Type of Security</th><th>Type of Dividend</th><th>Dividend Rate</th>" +
        "<th>Ex-Dividend Date</th><th>Record Date</th><th>Payment Date</th>" +
        "<th>Circular Number</th></tr></thead>" +
        $"<tbody>{string.Concat(rows)}</tbody></table>";

    private static (PseMarketDataProvider Provider, DividendsHandler Handler, FeedHealthRegistry Feed) Create(
        string? tableHtml,
        Action<MarketDataOptions>? configure = null)
    {
        var handler = new DividendsHandler(tableHtml);
        var feed = new FeedHealthRegistry();
        var options = new MarketDataOptions
        {
            PseEdgeSymbolSearchUrl = "https://edge.pse.com.ph/autoComplete/searchCompanyNameSymbol.ax",
            PseEdgeCompanyPageUrl = "https://edge.pse.com.ph/companyPage/stockData.do",
            PseEdgeChartUrl = "https://edge.pse.com.ph/common/DisclosureCht.ax",
            PseEdgeDividendsUrl = "https://edge.pse.com.ph/companyPage/dividends_and_rights_list.ax",
            PseEdgeAnnouncementsUrl = "https://edge.pse.com.ph/announcements/search.ax",
            RequestTimeoutSeconds = 5
        };

        configure?.Invoke(options);

        var provider = new PseMarketDataProvider(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<PseMarketDataProvider>.Instance,
            quotationParser: null,
            cache: new MarketDataCache(),
            feedHealth: feed);

        return (provider, handler, feed);
    }

    private static FeedHealth FeedOf(FeedHealthRegistry registry) =>
        registry.Snapshot().Single(feed => feed.Source == FeedSource.PseEdgeDividends);

    /// <summary>
    /// Answers the four Edge calls one research fetch makes. Yahoo is dark, exactly as it is for
    /// every PSE symbol today.
    /// </summary>
    private sealed class DividendsHandler : HttpMessageHandler
    {
        private readonly string? _tableHtml;

        public DividendsHandler(string? tableHtml) => _tableHtml = tableHtml;

        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Enqueue(uri);

            if (!uri.Contains("edge.pse.com.ph", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            if (uri.Contains("searchCompanyNameSymbol.ax", StringComparison.Ordinal))
            {
                return Json("[{\"cmpyId\":\"86\",\"cmpyNm\":\"Jollibee Foods Corporation\",\"symbol\":\"JFC\"}]");
            }

            if (uri.Contains("companyPage/stockData.do", StringComparison.Ordinal))
            {
                return Html(
                    "<select name=\"security_id\"><option value=\"158\" selected>JFC</option></select>" +
                    "<script>sendData.security_id = \"158\";</script>");
            }

            if (uri.Contains("common/DisclosureCht.ax", StringComparison.Ordinal))
            {
                return Json(ChartJson());
            }

            if (uri.Contains("dividends_and_rights_list.ax", StringComparison.Ordinal))
            {
                return _tableHtml is null
                    ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))
                    : Html(_tableHtml);
            }

            return Html("<html><body><table><tbody></tbody></table></body></html>");
        }

        private static string ChartJson()
        {
            var rows = new List<string>(Bars);

            for (var offset = Bars - 1; offset >= 0; offset--)
            {
                var close = 100m + (0.5m * (Bars - offset));

                rows.Add(
                    $"{{\"OPEN\":{Number(close - 0.5m)},\"VALUE\":12000000,\"CLOSE\":{Number(close)}," +
                    $"\"CHART_DATE\":\"{Session.AddDays(-offset).ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} 00:00:00\"," +
                    $"\"HIGH\":{Number(close + 0.5m)},\"LOW\":{Number(close - 1m)}}}");
            }

            return "{\"chartData\":[" + string.Join(",", rows) + "]}";
        }

        private static string Number(decimal value) =>
            value.ToString("0.####", CultureInfo.InvariantCulture);

        private static Task<HttpResponseMessage> Json(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });

        private static Task<HttpResponseMessage> Html(string body) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html")
            });
    }
}

