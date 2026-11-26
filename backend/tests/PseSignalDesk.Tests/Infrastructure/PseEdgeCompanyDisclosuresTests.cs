using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Diagnostics;
using PseSignalDesk.Infrastructure.MarketData;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The per-company disclosure pass. The markup below is the shape the exchange actually serves
/// (captured from <c>companyDisclosures/search.ax?keyword=86</c> on 2026-11-26): a subject cell
/// that is a link, an announce cell, then the PSE form and report numbers. Nothing here touches a
/// socket — the handler answers the URLs the fan-out uses.
/// </summary>
public sealed class PseEdgeCompanyDisclosuresTests
{
    private static readonly DateOnly Session = new(2026, 11, 26);

    [Fact]
    public async Task Resolves_each_ticker_through_the_exchange_company_id_and_returns_its_filings()
    {
        var (provider, handler, feed) = Create(uri =>
            uri.Contains("term=JFC", StringComparison.Ordinal)
                ? Json("[{\"cmpyId\":\"86\",\"cmpyNm\":\"Jollibee Foods Corporation\",\"symbol\":\"JFC\"}]")
                : uri.Contains("term=SM", StringComparison.Ordinal)
                    ? Json("[{\"cmpyId\":\"52\",\"cmpyNm\":\"SM Investments Corporation\",\"symbol\":\"SM\"}]")
                    : uri.Contains("keyword=86", StringComparison.Ordinal)
                        ? Html(CompanyPage(
                            Row("Nov 26, 2026 11:53 AM", "Change in Shareholdings of Directors and Principal Officers"),
                            Row("Nov 25, 2026 05:20 PM", "Stock Rights Offering")))
                        : uri.Contains("keyword=52", StringComparison.Ordinal)
                            ? Html(CompanyPage(Row("Nov 26, 2026 09:02 AM", "Press Release")))
                            : Empty());

        var result = await provider.FetchCompanyDisclosuresAsync(["JFC", "SM"], Session);

        Assert.Equal(2, result.Count);

        var jfc = result["JFC"];

        Assert.Equal(2, jfc.Count);

        // Newest first: the 26th filing precedes the 25th, whatever order the page printed.
        Assert.Equal("Change in Shareholdings of Directors and Principal Officers", jfc[0].Headline);
        Assert.Equal(new DateOnly(2026, 11, 26), jfc[0].Date);
        Assert.Equal(new TimeOnly(11, 53), jfc[0].FiledAtPht);
        Assert.Equal("Stock Rights Offering", jfc[1].Headline);
        Assert.Equal(new DateOnly(2026, 11, 25), jfc[1].Date);
        Assert.Equal(new TimeOnly(17, 20), jfc[1].FiledAtPht);
        Assert.All(jfc, disclosure => Assert.Equal("PSE Edge", disclosure.Source));

        Assert.Equal("Press Release", Assert.Single(result["SM"]).Headline);

        // The company id comes from the exchange, and the query is its own `keyword` field.
        Assert.Contains(handler.Requests, uri => uri.Contains("keyword=86", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, uri => uri.Contains("keyword=52", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, uri =>
            uri.Contains("companyDisclosures/search.ax", StringComparison.Ordinal) &&
            !uri.Contains("keyword=", StringComparison.Ordinal));

        Assert.Equal(FeedStatus.Ok, FeedOf(feed).Status);
        Assert.True(FeedOf(feed).Successes >= 2);
    }

    [Fact]
    public async Task Keeps_only_the_filings_inside_the_rolling_window()
    {
        // A page holds months of history; only the window the trap screen reads is carried.
        var (provider, _, _) = Create(uri => uri.Contains("term=", StringComparison.Ordinal)
            ? Json("[{\"cmpyId\":\"86\",\"symbol\":\"JFC\"}]")
            : Html(CompanyPage(
                Row("Nov 26, 2026 11:53 AM", "Stock Rights Offering"),
                Row("Mar 02, 2026 10:00 AM", "Press Release"),
                Row("11/26/2026", "Statement of Changes in Beneficial Ownership of Securities"))));

        var filings = (await provider.FetchCompanyDisclosuresAsync(["JFC"], Session))["JFC"];

        Assert.Equal(2, filings.Count);
        Assert.Contains(filings, filing => filing.Headline == "Stock Rights Offering");
        Assert.Contains(
            filings,
            filing => filing.Headline == "Statement of Changes in Beneficial Ownership of Securities" &&
                      filing.Date == new DateOnly(2026, 11, 26) &&
                      filing.FiledAtPht is null);
        Assert.DoesNotContain(filings, filing => filing.Headline == "Press Release");
    }

    [Fact]
    public async Task Drops_a_row_it_cannot_date_instead_of_back_dating_it_to_the_session()
    {
        var (provider, _, _) = Create(uri => uri.Contains("term=", StringComparison.Ordinal)
            ? Json("[{\"cmpyId\":\"86\",\"symbol\":\"JFC\"}]")
            : Html(CompanyPage(
                "<tr><td><a href=\"#viewer\">Mergers and Consolidations</a></td>" +
                "<td class=\"alignC\">pending</td><td class=\"alignC\">4-23</td></tr>",
                Row("Nov 26, 2026 11:53 AM", "Tender Offer"))));

        var filings = (await provider.FetchCompanyDisclosuresAsync(["JFC"], Session))["JFC"];

        Assert.Equal("Tender Offer", Assert.Single(filings).Headline);
    }

    [Fact]
    public async Task Answers_nothing_when_the_exchange_lists_no_company_page_and_does_not_throw()
    {
        var (provider, handler, feed) = Create(_ => Json("[]"));

        var result = await provider.FetchCompanyDisclosuresAsync(["NOPE"], Session);

        Assert.Empty(result);
        Assert.DoesNotContain(handler.Requests, uri => uri.Contains("keyword=", StringComparison.Ordinal));
        Assert.True(FeedOf(feed).Skips > 0);
        Assert.Equal(0, FeedOf(feed).Failures);
    }

    [Fact]
    public async Task Answers_nothing_and_reports_a_failure_when_the_company_page_is_unreachable()
    {
        var (provider, _, feed) = Create(uri => uri.Contains("term=", StringComparison.Ordinal)
            ? Json("[{\"cmpyId\":\"86\",\"symbol\":\"JFC\"}]")
            : Status(HttpStatusCode.InternalServerError));

        var result = await provider.FetchCompanyDisclosuresAsync(["JFC"], Session);

        Assert.Empty(result);

        var disclosures = FeedOf(feed);

        Assert.Equal(FeedStatus.Failed, disclosures.Status);
        Assert.True(disclosures.Failures > 0);
        Assert.Contains("500", disclosures.LastDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Does_nothing_when_the_fanout_limit_is_zero()
    {
        // The documented rollback: the run keeps the market-wide scrape and makes no extra request.
        var (provider, handler, _) = Create(_ => Json("[]"), options => options.DisclosureCompanyFanoutLimit = 0);

        var result = await provider.FetchCompanyDisclosuresAsync(["JFC", "SM"], Session);

        Assert.Empty(result);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Caps_the_fanout_at_the_configured_limit()
    {
        var tickers = Enumerable.Range(1, 8).Select(index => $"T{index}").ToList();

        var (provider, handler, _) = Create(
            uri =>
            {
                if (!uri.Contains("term=", StringComparison.Ordinal))
                {
                    return Html(CompanyPage(Row("Nov 26, 2026 11:53 AM", "Press Release")));
                }

                // A distinct company id per ticker, so the page cache cannot collapse the fan-out.
                var term = uri[(uri.IndexOf("term=", StringComparison.Ordinal) + 5)..];

                return Json($"[{{\"cmpyId\":\"{term[1..]}\",\"symbol\":\"{term}\"}}]");
            },
            options => options.DisclosureCompanyFanoutLimit = 3);

        await provider.FetchCompanyDisclosuresAsync(tickers, Session);

        var pages = handler.Requests
            .Where(uri => uri.Contains("companyDisclosures/search.ax", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(3, pages.Count);
        Assert.All(pages, uri => Assert.Contains("keyword=", uri, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reuses_the_cached_page_and_the_cached_company_id_across_calls()
    {
        var (provider, handler, _) = Create(uri => uri.Contains("term=", StringComparison.Ordinal)
            ? Json("[{\"cmpyId\":\"86\",\"symbol\":\"JFC\"}]")
            : Html(CompanyPage(Row("Nov 26, 2026 11:53 AM", "Press Release"))));

        await provider.FetchCompanyDisclosuresAsync(["JFC"], Session);
        var afterFirst = handler.Requests.Count;

        await provider.FetchCompanyDisclosuresAsync(["JFC"], Session);

        Assert.Equal(2, afterFirst);
        Assert.Equal(afterFirst, handler.Requests.Count);
    }


    /// <summary>One company-page row in the exchange's own markup.</summary>
    private static string Row(string announceCell, string subject) =>
        $"<tr><td><a href=\"#viewer\" onclick=\"openPopup('abc');return false;\">{subject}</a></td>" +
        $"<td class=\"alignC\">{announceCell}</td>" +
        "<td class=\"alignC\">4-23</td><td class=\"alignC\">C08413-2026</td></tr>";

    private static string CompanyPage(params string[] rows) =>
        "<span class=\"count\">[1 / 9] [Total 424]</span>" +
        "<table class=\"list\"><thead><tr><th>Template Name</th><th>Announce Date and Time</th>" +
        "<th>PSE Form Number</th><th>Report or Circular Number</th></tr></thead>" +
        $"<tbody>{string.Concat(rows)}</tbody></table>";

    private static (PseMarketDataProvider Provider, StubHandler Handler, FeedHealthRegistry Feed) Create(
        Func<string, HttpResponseMessage> respond,
        Action<MarketDataOptions>? configure = null)
    {
        var handler = new StubHandler(respond);
        var feed = new FeedHealthRegistry();
        var options = new MarketDataOptions
        {
            PseEdgeSymbolSearchUrl = "https://edge.pse.com.ph/autoComplete/searchCompanyNameSymbol.ax",
            PseEdgeCompanyDisclosuresUrl = "https://edge.pse.com.ph/companyDisclosures/search.ax",
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
        registry.Snapshot().Single(feed => feed.Source == FeedSource.PseEdgeDisclosures);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Html(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Empty() => new(HttpStatusCode.NotFound);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<string, HttpResponseMessage> _respond;

        public StubHandler(Func<string, HttpResponseMessage> respond) => _respond = respond;

        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;
            Requests.Enqueue(uri);

            return Task.FromResult(_respond(uri));
        }
    }

}
