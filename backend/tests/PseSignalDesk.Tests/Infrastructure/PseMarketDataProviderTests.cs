using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.MarketData;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The provider is live-only, so these tests drive it with a stubbed
/// <see cref="HttpMessageHandler"/>: no socket is opened and no response is invented by
/// the production code, which keeps the offline test guarantee intact.
/// </summary>
public sealed class PseMarketDataProviderTests
{
    private static readonly DateOnly TradeDate = new(2026, 11, 24);

    [Fact]
    public async Task FetchAsync_propagates_caller_cancellation_instead_of_using_a_fallback_series()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var provider = CreateProvider(new StubHandler((_, token) =>
            Task.FromCanceled<HttpResponseMessage>(token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.FetchAsync(TradeDate, aborted.Token));
    }

    [Fact]
    public async Task FetchAsync_fails_when_yahoo_returns_a_non_success_status()
    {
        var provider = CreateProvider(new StubHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            })));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.FetchAsync(TradeDate));

        Assert.Contains("429", error.Message, StringComparison.Ordinal);
        Assert.Contains("PSEI.PS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_fails_when_the_index_symbol_is_not_published()
    {
        var provider = CreateProvider(new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.FetchAsync(TradeDate));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_fails_when_the_index_session_is_stale()
    {
        var provider = CreateProvider(new LiveFeedHandler(TradeDate.AddDays(-30)));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.FetchAsync(TradeDate));

        Assert.Contains("days before the requested", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_fails_when_pse_edge_returns_an_empty_page()
    {
        var provider = CreateProvider(new LiveFeedHandler(TradeDate, edgeHtml: "   "));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.FetchAsync(TradeDate));

        Assert.Contains("PSE Edge", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_builds_a_live_batch_from_yahoo_charts_and_pse_edge()
    {
        var provider = CreateProvider(new LiveFeedHandler(TradeDate));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.Equal("yahoo-finance+pse-edge", batch.Source);
        Assert.Null(batch.NetForeignFlowPhp);
        Assert.Equal(MarketRegime.BULLISH, batch.MarketRegime);
        Assert.True(batch.PseiClose > 8000m);
        Assert.True(batch.UsdPhp > 58m);
        Assert.Contains("Net foreign flow is unreported", batch.RegimeNote, StringComparison.Ordinal);

        Assert.True(batch.Stocks.Count >= 10, $"only {batch.Stocks.Count} tickers survived the live filter");
        Assert.All(batch.Stocks, stock => Assert.Null(stock.NetForeignTodayPhp));
        Assert.All(batch.Stocks, stock => Assert.Equal(TradeDate, stock.LatestBar!.Date));
        Assert.All(batch.Stocks, stock => Assert.True(stock.Bars.Count >= QuantitativeFilterService.MinBarsRequired));

        // The stub index page carries one real-looking headline for SM Investments, so the
        // disclosure matcher must attribute it to SM.
        Assert.Contains(batch.Stocks, stock =>
            stock.Ticker == "SM" &&
            stock.Disclosures.Any(disclosure =>
                disclosure.Headline.Contains("cash dividend", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task FetchAsync_rounds_every_candle_onto_the_pse_tick_grid()
    {
        var provider = CreateProvider(new LiveFeedHandler(
            TradeDate,
            stockStartPrice: 20.37m,
            stockStep: 0.013m));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.All(batch.Stocks, stock => Assert.All(stock.Bars, bar =>
        {
            Assert.Equal(bar.Close, PseBoardLotService.RoundToValidTick(bar.Close));
            Assert.True(bar.High >= Math.Max(bar.Open, bar.Close));
            Assert.True(bar.Low <= Math.Min(bar.Open, bar.Close));
            Assert.True(bar.Close > 0m);
        }));
    }

    [Fact]
    public async Task FetchAsync_reads_paid_dividends_and_drops_an_announced_future_ex_date()
    {
        var provider = CreateProvider(new LiveFeedHandler(TradeDate, includeDividends: true));

        var batch = await provider.FetchAsync(TradeDate);

        var paidExDate = TradeDate.AddDays(-14);
        Assert.All(batch.Stocks, stock =>
        {
            var dividend = Assert.Single(stock.Dividends!);
            Assert.Equal(paidExDate, dividend.ExDate);
            Assert.Equal(1.25m, dividend.AmountPerShare);
        });
    }

    [Fact]
    public async Task FetchAsync_leaves_dividends_empty_when_the_feed_reports_none()
    {
        var provider = CreateProvider(new LiveFeedHandler(TradeDate));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.All(batch.Stocks, stock => Assert.Empty(stock.Dividends!));
    }

    [Fact]
    public async Task FetchAsync_keeps_the_macro_dials_unreported_when_a_yahoo_series_fails()
    {
        var live = new LiveFeedHandler(TradeDate);
        var notFound = new InlineHandler(string.Empty, HttpStatusCode.NotFound);

        var provider = CreateProvider(new SelectiveHandler(
            IsMacroSymbol,
            notFound.SendForTest,
            live.RespondAsync));

        var batch = await provider.FetchAsync(TradeDate);

        // A macro outage must never block the live PSE tape, and must never become a zero.
        Assert.Null(batch.Us10YClosePct);
        Assert.Null(batch.Us10YChange20dBps);
        Assert.Null(batch.BrentUsd);
        Assert.Null(batch.BrentChange20dPct);
        Assert.Null(batch.EpheCloseUsd);
        Assert.Null(batch.EpheChange20dPct);
        Assert.True(batch.Stocks.Count >= 10);
    }

    [Fact]
    public async Task FetchAsync_computes_the_cross_asset_levels_and_their_twenty_session_changes()
    {
        var provider = CreateProvider(new LiveFeedHandler(TradeDate));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.NotNull(batch.Us10YClosePct);
        Assert.NotNull(batch.Us10YChange20dBps);
        Assert.NotNull(batch.BrentUsd);
        Assert.NotNull(batch.BrentChange20dPct);
        Assert.NotNull(batch.EpheCloseUsd);
        Assert.NotNull(batch.EpheChange20dPct);

        // The stub series rises monotonically, so the yield (in bps) and both dollar series
        // must print a positive 20-session change.
        Assert.True(batch.Us10YChange20dBps!.Value > 0m);
        Assert.True(batch.BrentChange20dPct!.Value > 0m);
        Assert.True(batch.EpheChange20dPct!.Value > 0m);

        // No DQR is configured here, so foreign flow stays honestly unreported.
        Assert.Null(batch.NetForeignFlowPhp);
        Assert.Null(batch.ForeignFlowSource);
    }

    [Fact]
    public async Task FetchAsync_overlays_the_daily_quotation_report_and_records_its_provenance()
    {
        var dqr = string.Join('\n', new[]
        {
            "Ticker,Security Name,Close,Value,Net Foreign",
            "SM,SM Investments,850.00,900000000,22500000",
            "ICT,Intl Container,430.00,1250000000,\"(12,500,000.00)\"",
            "TEL,PLDT,1300.00,0,SUSPENDED",
            "Net Foreign Total,,,,40000000"
        });

        var live = new LiveFeedHandler(TradeDate);

        var provider = CreateProvider(
            new SelectiveHandler(
                IsQuotationReportRequest,
                new InlineHandler(dqr).SendForTest,
                live.RespondAsync),
            options => options.PseDailyQuotationUrlTemplates.Add(
                "https://files.example.test/dqr/{yyyy}/{MMddyyyy}.csv"));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.Equal(40_000_000m, batch.NetForeignFlowPhp!.Value);
        Assert.Equal("pse-dqr-live", batch.ForeignFlowSource);
        Assert.Contains("pse-dqr", batch.Source, StringComparison.Ordinal);

        Assert.Equal(22_500_000m, batch.Stocks.Single(stock => stock.Ticker == "SM").NetForeignTodayPhp!.Value);
        Assert.Equal(-12_500_000m, batch.Stocks.Single(stock => stock.Ticker == "ICT").NetForeignTodayPhp!.Value);

        // A ticker the report never mentions keeps the honest null.
        Assert.Null(batch.Stocks.Single(stock => stock.Ticker == "BDO").NetForeignTodayPhp);

        var tel = batch.Stocks.Single(stock => stock.Ticker == "TEL");
        Assert.Contains(tel.Disclosures, disclosure =>
            disclosure.Headline.Contains("suspended", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchAsync_treats_a_missing_daily_quotation_report_as_unreported()
    {
        var live = new LiveFeedHandler(TradeDate);

        var provider = CreateProvider(
            new SelectiveHandler(
                IsQuotationReportRequest,
                new InlineHandler(string.Empty, HttpStatusCode.NotFound).SendForTest,
                live.RespondAsync),
            options => options.PseDailyQuotationUrlTemplates.Add(
                "https://files.example.test/dqr/{MMddyyyy}.csv"));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.Null(batch.NetForeignFlowPhp);
        Assert.Null(batch.ForeignFlowSource);
        Assert.All(batch.Stocks, stock => Assert.Null(stock.NetForeignTodayPhp));
        Assert.DoesNotContain("pse-dqr", batch.Source, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_falls_through_to_the_secondary_yahoo_host()
    {
        var live = new LiveFeedHandler(TradeDate);

        // The primary origin is down; the mirror serves the same payload, so the desk keeps a run.
        var provider = CreateProvider(new SelectiveHandler(
            request => (request.RequestUri?.Host ?? string.Empty) == "query1.finance.yahoo.com",
            new InlineHandler(
                "{\"chart\":{\"result\":null,\"error\":\"unavailable\"}}",
                HttpStatusCode.ServiceUnavailable).SendForTest,
            live.RespondAsync));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.Equal(TradeDate, batch.TradeDate);
        Assert.True(batch.Stocks.Count >= 10, $"only {batch.Stocks.Count} tickers survived the host fallback");
        Assert.Equal(MarketRegime.BULLISH, batch.MarketRegime);
    }

    [Fact]
    public async Task FetchAsync_does_not_date_the_session_on_a_zero_volume_echo_row()
    {
        // Yahoo echoes a flat, zero-volume row for the holiday itself: storing it would date the
        // whole run on a session that never traded and push a phantom flat bar into the averages.
        var provider = CreateProvider(new LiveFeedHandler(TradeDate, zeroVolumeEchoes: 1));

        var batch = await provider.FetchAsync(TradeDate);

        var indexBars = Assert.IsAssignableFrom<IReadOnlyList<OhlcvBar>>(batch.IndexBars);

        Assert.Equal(TradeDate.AddDays(-1), batch.TradeDate);
        Assert.Equal(TradeDate.AddDays(-1), indexBars[^1].Date);
        Assert.All(indexBars, bar => Assert.True(bar.Volume > 0, $"{bar.Date:yyyy-MM-dd} carried no volume"));
        Assert.All(batch.Stocks, stock => Assert.Equal(TradeDate.AddDays(-1), stock.LatestBar!.Date));
    }

    [Fact]
    public async Task FetchAsync_drops_the_unfinished_index_candle_while_the_session_is_running()
    {
        // The regular session is still open, so the newest index candle is a snapshot rather than
        // a settled bar and must not decide the trend or date the run.
        var provider = CreateProvider(new LiveFeedHandler(
            TradeDate,
            sessionStillRunningAt: new TimeOnly(10, 15)));

        var batch = await provider.FetchAsync(TradeDate);

        var indexBars = Assert.IsAssignableFrom<IReadOnlyList<OhlcvBar>>(batch.IndexBars);

        Assert.Equal(TradeDate.AddDays(-1), batch.TradeDate);
        Assert.DoesNotContain(indexBars, bar => bar.Date == TradeDate);
        Assert.All(batch.Stocks, stock => Assert.Equal(TradeDate.AddDays(-1), stock.LatestBar!.Date));
    }

    [Fact]
    public async Task FetchAsync_attributes_a_filing_by_its_row_and_never_by_a_substring()
    {
        // "auditor" contains the letters of DITO, so a substring match would hand BPI's filing to
        // the telecom challenger. The company/subject row pairing is the attribution key.
        var html = "<table>" +
                   "<tr><td>11/24/2026</td><td><a href=\"#\">DITO CME Holdings Corp.</a></td>" +
                   "<td><a href=\"#\">Notice of Annual Stockholders Meeting</a></td></tr>" +
                   "<tr><td>11/24/2026</td><td><a href=\"#\">Bank of the Philippine Islands</a></td>" +
                   "<td><a href=\"#\">Change in Certifying Public Auditor</a></td></tr>" +
                   "</table>";

        var provider = CreateProvider(new LiveFeedHandler(TradeDate, edgeHtml: html));

        var batch = await provider.FetchAsync(TradeDate);

        var dito = batch.Stocks.Single(stock => stock.Ticker == "DITO");
        var bpi = batch.Stocks.Single(stock => stock.Ticker == "BPI");

        Assert.Contains(dito.Disclosures, disclosure =>
            disclosure.Headline.Contains("Stockholders Meeting", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dito.Disclosures, disclosure =>
            disclosure.Headline.Contains("Auditor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(bpi.Disclosures, disclosure =>
            disclosure.Headline.Contains("Stockholders Meeting", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task FetchAsync_reads_the_regime_from_the_heavyweight_proxy_when_the_index_has_no_history()
    {
        // Yahoo answers PSEI.PS with a single bar and no history, which is why the trend has to
        // come from real constituent closes instead.
        var live = new LiveFeedHandler(TradeDate);

        var provider = CreateProvider(new SelectiveHandler(
            request => (request.RequestUri?.ToString() ?? string.Empty)
                .Contains("PSEI.PS", StringComparison.Ordinal),
            new InlineHandler(SingleBarChartJson(TradeDate)).SendForTest,
            live.RespondAsync));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.Equal(TradeDate, batch.TradeDate);
        Assert.Equal(MarketRegime.BULLISH, batch.MarketRegime);

        var indexBars = Assert.IsAssignableFrom<IReadOnlyList<OhlcvBar>>(batch.IndexBars);
        Assert.True(indexBars.Count >= 200);

        // A proxy is scale-free: it can drive the regime, breadth and volatility, but it must
        // never print an index level or an index moving average, so both stay unreported (0).
        Assert.Equal(0m, batch.PseiSma50);
        Assert.Equal(0m, batch.PseiSma200);
        Assert.Equal(0m, batch.PseiClose);
        Assert.Contains("proxy", batch.RegimeNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_serves_a_repeated_session_from_the_cache_instead_of_refetching_yahoo()
    {
        // The daily run, a manual run and the research panel ask for the same chart URLs within
        // minutes of each other; only the first of them may cost a download.
        var live = new LiveFeedHandler(TradeDate);
        var requests = 0;

        var provider = CreateProvider(new StubHandler((request, token) =>
        {
            Interlocked.Increment(ref requests);
            return live.RespondAsync(request, token);
        }));

        var first = await provider.FetchAsync(TradeDate);
        var afterFirstRun = requests;

        var second = await provider.FetchAsync(TradeDate);

        Assert.True(afterFirstRun >= 10, $"the first run only fetched {afterFirstRun} URLs");
        Assert.Equal(afterFirstRun, requests);
        Assert.Equal(first.TradeDate, second.TradeDate);
        Assert.Equal(
            first.Stocks.Select(stock => stock.Ticker),
            second.Stocks.Select(stock => stock.Ticker));
    }

    [Fact]
    public async Task InvalidateCache_forces_the_next_run_to_refetch_the_live_tape()
    {
        var live = new LiveFeedHandler(TradeDate);
        var requests = 0;

        var provider = CreateProvider(new StubHandler((request, token) =>
        {
            Interlocked.Increment(ref requests);
            return live.RespondAsync(request, token);
        }));

        await provider.FetchAsync(TradeDate);
        var afterFirstRun = requests;

        provider.InvalidateCache();
        await provider.FetchAsync(TradeDate);

        Assert.True(
            requests > afterFirstRun,
            $"invalidation did not refetch: {requests} requests, {afterFirstRun} before");
    }

    /// <summary>
    /// The one-bar index payload the live feed really returns for <c>PSEI.PS</c>: a price with no
    /// history, which is not enough to describe a 200-session trend.
    /// </summary>
    private static string SingleBarChartJson(DateOnly date)
    {
        var stamp = new DateTimeOffset(date.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc))
            .ToUnixTimeSeconds();

        return "{\"chart\":{\"result\":[{\"meta\":{\"symbol\":\"PSEI.PS\"},\"timestamp\":[" +
               stamp.ToString(CultureInfo.InvariantCulture) +
               "],\"indicators\":{\"quote\":[{\"open\":[8000],\"high\":[8010],\"low\":[7990]," +
               "\"close\":[8005],\"volume\":[1200000]}]}}],\"error\":null}}";
    }

    private static PseMarketDataProvider CreateProvider(HttpMessageHandler handler) =>
        CreateProvider(handler, configure: null);

    private static PseMarketDataProvider CreateProvider(
        HttpMessageHandler handler,
        Action<MarketDataOptions>? configure)
    {
        var options = new MarketDataOptions
        {
            YahooFinanceBaseUrl = "https://query1.finance.yahoo.com",
            PseEdgeAnnouncementsUrl = "https://edge.pse.com.ph/announcements/search.ax",
            LookbackCalendarDays = 420,
            MaxConcurrency = 4,
            RequestTimeoutSeconds = 5
        };

        configure?.Invoke(options);

        return new PseMarketDataProvider(
            new HttpClient(handler),
            Options.Create(options),
            NullLogger<PseMarketDataProvider>.Instance);
    }

    private static bool IsMacroSymbol(HttpRequestMessage request)
    {
        var path = Uri.UnescapeDataString(request.RequestUri?.AbsolutePath ?? string.Empty);

        return path.Contains("TNX", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("BZ=F", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("EPHE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsQuotationReportRequest(HttpRequestMessage request) =>
        (request.RequestUri?.Host ?? string.Empty).Contains("files.example.test", StringComparison.Ordinal);

    /// <summary>
    /// Sends the requests matching <paramref name="predicate"/> to <paramref name="matched"/> and
    /// everything else to <paramref name="fallback"/>, so one test can fail a single feed.
    /// </summary>
    private sealed class SelectiveHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, bool> _predicate;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _matched;
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _fallback;

        public SelectiveHandler(
            Func<HttpRequestMessage, bool> predicate,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> matched,
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> fallback)
        {
            _predicate = predicate;
            _matched = matched;
            _fallback = fallback;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            (_predicate(request) ? _matched : _fallback)(request, cancellationToken);
    }

    /// <summary>Answers with a fixed body and status, used for the DQR and the 404 cases.</summary>
    private sealed class InlineHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public InlineHandler(string body, HttpStatusCode status = HttpStatusCode.OK)
        {
            _body = body;
            _status = status;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "text/plain")
            });

        /// <summary>Allows a composing handler to reuse this stub's fixed response.</summary>
        public Task<HttpResponseMessage> SendForTest(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => _responder(request, cancellationToken);
    }

    /// <summary>
    /// Answers every Yahoo chart request with a deterministic rising series ending on
    /// <paramref name="endDate"/>, and the PSE Edge page with one headline.
    /// </summary>
    private sealed class LiveFeedHandler : HttpMessageHandler
    {
        private readonly DateOnly _endDate;
        private readonly string _edgeHtml;
        private readonly decimal _stockStartPrice;
        private readonly decimal _stockStep;
        private readonly bool _includeDividends;
        private readonly int _zeroVolumeEchoes;
        private readonly TimeOnly? _sessionStillRunningAt;

        public LiveFeedHandler(
            DateOnly endDate,
            string? edgeHtml = null,
            decimal stockStartPrice = 20m,
            decimal stockStep = 0.1m,
            bool includeDividends = false,
            int zeroVolumeEchoes = 0,
            TimeOnly? sessionStillRunningAt = null)
        {
            _endDate = endDate;
            _edgeHtml = edgeHtml ??
                "<html><body><a href=\"#\">SM Investments Corporation declares cash dividend</a></body></html>";
            _stockStartPrice = stockStartPrice;
            _stockStep = stockStep;
            _includeDividends = includeDividends;
            _zeroVolumeEchoes = zeroVolumeEchoes;
            _sessionStillRunningAt = sessionStillRunningAt;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            RespondAsync(request, cancellationToken);

        /// <summary>
        /// Public so a composing handler can delegate to the live stub without duplicating the
        /// chart/edge fixtures.
        /// </summary>
        public Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var uri = request.RequestUri?.ToString() ?? string.Empty;

            if (uri.Contains("edge.pse.com.ph", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_edgeHtml, Encoding.UTF8, "text/html")
                });
            }

            var body = uri.Contains("PHP%3DX", StringComparison.Ordinal)
                ? BuildChartJson(_endDate, bars: 220, start: 58m, step: 0.01m)
                : uri.Contains("PSEI.PS", StringComparison.Ordinal)
                    ? BuildChartJson(
                        _endDate,
                        bars: 220,
                        start: 8_000m,
                        step: 2m,
                        zeroVolumeEchoes: _zeroVolumeEchoes,
                        sessionStillRunningAt: _sessionStillRunningAt)
                    : BuildChartJson(
                        _endDate,
                        bars: 220,
                        start: _stockStartPrice,
                        step: _stockStep,
                        includeDividends: _includeDividends,
                        zeroVolumeEchoes: _zeroVolumeEchoes,
                        sessionStillRunningAt: _sessionStillRunningAt);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static string BuildChartJson(
            DateOnly endDate,
            int bars,
            decimal start,
            decimal step,
            bool includeDividends = false,
            int zeroVolumeEchoes = 0,
            TimeOnly? sessionStillRunningAt = null)
        {
            var seriesEnd = endDate.AddDays(-zeroVolumeEchoes);
            var lastClose = start + (step * (bars - 1));
            var total = bars + zeroVolumeEchoes;

            var timestamps = new StringBuilder(total * 12);
            var opens = new StringBuilder(total * 8);
            var highs = new StringBuilder(total * 8);
            var lows = new StringBuilder(total * 8);
            var closes = new StringBuilder(total * 8);
            var volumes = new StringBuilder(total * 10);
            var first = true;

            void Append(DateOnly date, decimal open, decimal high, decimal low, decimal close, long volume)
            {
                if (!first)
                {
                    timestamps.Append(',');
                    opens.Append(',');
                    highs.Append(',');
                    lows.Append(',');
                    closes.Append(',');
                    volumes.Append(',');
                }

                first = false;

                var stamp = new DateTimeOffset(date.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc))
                    .ToUnixTimeSeconds();

                timestamps.Append(stamp.ToString(CultureInfo.InvariantCulture));
                opens.Append(Number(open));
                highs.Append(Number(high));
                lows.Append(Number(low));
                closes.Append(Number(close));
                volumes.Append(volume.ToString(CultureInfo.InvariantCulture));
            }

            for (var index = 0; index < bars; index++)
            {
                var close = start + (step * index);

                Append(
                    seriesEnd.AddDays(index - (bars - 1)),
                    close - 0.02m,
                    close + 0.05m,
                    close - 0.05m,
                    close,
                    1_000_000 + (index * 1_000));
            }

            // Yahoo prints a flat, zero-volume row for a session with no trades (a PSE holiday, a
            // suspended name). It repeats the previous close, which is exactly the placeholder the
            // provider is supposed to drop before the bar can date the run.
            for (var echo = 1; echo <= zeroVolumeEchoes; echo++)
            {
                Append(seriesEnd.AddDays(echo), lastClose, lastClose, lastClose, lastClose, 0L);
            }

            var metaExtra = new StringBuilder();

            if (sessionStillRunningAt is TimeOnly runningAt)
            {
                var marketTime = new DateTimeOffset(
                    endDate.ToDateTime(runningAt, DateTimeKind.Unspecified),
                    TimeSpan.FromHours(8)).ToUnixTimeSeconds();

                metaExtra.Append(",\"regularMarketPrice\":").Append(Number(lastClose));
                metaExtra.Append(",\"regularMarketTime\":")
                    .Append(marketTime.ToString(CultureInfo.InvariantCulture));
                metaExtra.Append(",\"currentTradingPeriod\":{\"regular\":{\"end\":")
                    .Append((marketTime + 1_800).ToString(CultureInfo.InvariantCulture))
                    .Append("}}");
            }

            var json = new StringBuilder(total * 64);
            json.Append("{\"chart\":{\"result\":[{\"meta\":{\"symbol\":\"STUB\"")
                .Append(metaExtra)
                .Append("},\"timestamp\":[")
                .Append(timestamps)
                .Append("],\"indicators\":{\"quote\":[{\"open\":[")
                .Append(opens)
                .Append("],\"high\":[")
                .Append(highs)
                .Append("],\"low\":[")
                .Append(lows)
                .Append("],\"close\":[")
                .Append(closes)
                .Append("],\"volume\":[")
                .Append(volumes)
                .Append("]}]}")
                .Append(includeDividends ? BuildDividendsJson(endDate) : string.Empty)
                .Append("}],\"error\":null}}");

            return json.ToString();
        }

        /// <summary>
        /// Yahoo's <c>events=div</c> payload: one paid dividend dated before the last bar,
        /// one duplicate of the same ex-date carrying a smaller amount, and one announced
        /// future ex-date that is not a trailing-twelve-month payment yet.
        /// </summary>
        private static string BuildDividendsJson(DateOnly endDate)
        {
            var paidDate = endDate.AddDays(-14);
            var futureDate = endDate.AddDays(20);
            var paidStamp = new DateTimeOffset(paidDate.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc))
                .ToUnixTimeSeconds();
            var futureStamp = new DateTimeOffset(futureDate.ToDateTime(new TimeOnly(3, 0), DateTimeKind.Utc))
                .ToUnixTimeSeconds();

            return ",\"events\":{\"dividends\":{" +
                   $"\"{paidDate:yyyy-MM-dd}\":{{\"amount\":1.25,\"date\":{paidStamp}}}," +
                   $"\"{paidDate:yyyy-MM-dd}-dup\":{{\"amount\":0.75,\"date\":{paidStamp}}}," +
                   $"\"{futureDate:yyyy-MM-dd}\":{{\"amount\":9.99,\"date\":{futureStamp}}}" +
                   "}}";
        }

        private static string Number(decimal value) =>
            value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
