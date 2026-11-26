using System.Collections.Concurrent;
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

    /// <summary>
    /// The heavyweights <see cref="MarketDataOptions"/> initializes, in the order the proxy fetches
    /// them. The shipped <c>appsettings.json</c> repeats this same list, which is why the options
    /// are normalised before anything is fetched.
    /// </summary>
    private static readonly string[] DefaultProxyConstituents =
    {
        "TEL", "SM", "SMPH", "ALI", "BDO", "BPI", "ICT", "JFC", "MER", "AC", "URC", "GLO"
    };

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

    [Fact]
    public async Task FetchAsync_counts_each_proxy_constituent_once_when_configuration_repeats_it()
    {
        // ConfigurationBinder adds a bound section to the options' initialized lists, so a section
        // that repeats the code defaults doubles both lists. The run must still read the trend from
        // 12 distinct heavyweights (not 24 names fetched twice) and print that honest count.
        var live = new LiveFeedHandler(TradeDate);
        var requests = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var routed = new SelectiveHandler(
            request => (request.RequestUri?.ToString() ?? string.Empty)
                .Contains("PSEI.PS", StringComparison.Ordinal),
            new InlineHandler(SingleBarChartJson(TradeDate)).SendForTest,
            live.RespondAsync);

        var provider = CreateProvider(
            new StubHandler((request, token) =>
            {
                requests.AddOrUpdate(request.RequestUri?.ToString() ?? string.Empty, 1, (_, count) => count + 1);
                return routed.SendForTest(request, token);
            }),
            options =>
            {
                options.IndexYahooSymbols.Add(options.IndexYahooSymbols[0]);
                options.IndexProxyConstituents = options.IndexProxyConstituents
                    .Concat(options.IndexProxyConstituents)
                    .ToList();
            });

        var batch = await provider.FetchAsync(TradeDate);

        Assert.Contains(
            $"equal-weight proxy of {DefaultProxyConstituents.Length} PSE heavyweights",
            batch.RegimeNote,
            StringComparison.Ordinal);

        Assert.Equal(
            1,
            requests.Count(entry => entry.Key.Contains("PSEI.PS", StringComparison.Ordinal)));
        Assert.Equal(
            1,
            requests.Count(entry => entry.Key.Contains("/chart/TEL.PS", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FetchAsync_reports_each_distinct_proxy_constituent_once_in_configured_order()
    {
        // Every symbol and constituent 404s: the diagnostic must name 12 distinct heavyweights, in
        // the configured order, instead of repeating the defaults the binder appended.
        var provider = CreateProvider(
            new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))),
            options =>
            {
                options.IndexYahooSymbols.Add(options.IndexYahooSymbols[0]);
                options.IndexProxyConstituents = options.IndexProxyConstituents
                    .Concat(options.IndexProxyConstituents)
                    .ToList();
            });

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.FetchAsync(TradeDate));

        Assert.Contains(
            $"could only use 0 of {DefaultProxyConstituents.Length} constituents",
            error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain($"{DefaultProxyConstituents.Length * 2} constituents", error.Message, StringComparison.Ordinal);

        // One entry per constituent, no repeats, in the order they are configured to be fetched.
        Assert.Equal(1, CountOccurrences(error.Message, "SM: HTTP 404"));
        Assert.True(
            error.Message.IndexOf("TEL: HTTP 404", StringComparison.Ordinal) <
            error.Message.IndexOf("SM: HTTP 404", StringComparison.Ordinal),
            error.Message);
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

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;

        while (index < text.Length &&
               (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    [Fact]
    public async Task FetchSingleStockAsync_reads_the_series_from_pse_edge_when_yahoo_delists_the_symbol()
    {
        // Yahoo answers every {TICKER}.PS with 404 ("No data found, symbol may be delisted"),
        // which is exactly the state of the live feed. The exchange's own Edge history is the
        // only live series left, so the desk has to read it instead of failing the ticker.
        var provider = CreateProvider(new DarkYahooHandler(new EdgeFeedHandler(TradeDate)));

        var stock = await provider.FetchSingleStockAsync("JFC", TradeDate);

        Assert.NotNull(stock);
        Assert.Equal("JFC", stock!.Ticker);
        Assert.Equal(220, stock.Bars.Count);
        Assert.Equal(TradeDate, stock.LatestBar!.Date);
        Assert.Equal(TradeDate, stock.Bars[^1].Date);
        Assert.All(stock.Bars, bar => Assert.True(bar.Close > 0m && bar.Volume > 0));
    }

    [Fact]
    public async Task FetchSingleStockAsync_derives_share_volume_from_the_reported_peso_value()
    {
        // Edge publishes the peso value traded, not the share count. VALUE / typical price has to
        // reproduce the exchange's own volume, because Close x Volume is what the PHP 5M
        // liquidity floor is measured on.
        const decimal valuePerBar = 12_000_000m;
        var provider = CreateProvider(new DarkYahooHandler(
            new EdgeFeedHandler(TradeDate, valuePerBar: valuePerBar)));

        var stock = await provider.FetchSingleStockAsync("JFC", TradeDate);

        Assert.NotNull(stock);
        Assert.All(stock!.Bars, bar =>
        {
            var typicalPrice = (bar.High + bar.Low + bar.Close) / 3m;
            var expected = (long)Math.Round(valuePerBar / typicalPrice, MidpointRounding.AwayFromZero);

            Assert.Equal(expected, bar.Volume);

            // Close x Volume reproduces the official turnover the liquidity floor uses, within
            // the drift the typical-price approximation can introduce.
            var turnover = bar.Close * bar.Volume;
            Assert.True(
                Math.Abs(turnover - valuePerBar) <= valuePerBar * 0.02m,
                $"turnover {turnover} drifted from the reported {valuePerBar}");
        });
    }

    [Fact]
    public async Task FetchSingleStockAsync_collapses_repeated_edge_sessions_and_keeps_the_series_ordered()
    {
        // Edge repeats a session row now and then (JFC prints 2025-11-27 twice). One bar per
        // session has to survive, in ascending order, with the last print winning.
        var provider = CreateProvider(new DarkYahooHandler(
            new EdgeFeedHandler(TradeDate, bars: 220, duplicateSessions: 1)));

        var stock = await provider.FetchSingleStockAsync("JFC", TradeDate);

        Assert.NotNull(stock);

        var dates = stock!.Bars.Select(bar => bar.Date).ToList();

        Assert.Equal(220, dates.Count);
        Assert.Equal(dates.Distinct().Count(), dates.Count);
        Assert.Equal(dates.OrderBy(date => date), dates);

        // The repeated session's later print is the one that survives.
        var repeated = Assert.Single(stock.Bars, bar => bar.Date == TradeDate.AddDays(-1));
        Assert.Equal(1.5m, repeated.Close);
    }

    [Fact]
    public async Task FetchAsync_names_both_feeds_when_neither_publishes_a_psei_series()
    {
        // Neither feed may be silently dropped from the diagnostic: an operator has to be able to
        // see that Yahoo 404s and that Edge did not answer either.
        var provider = CreateProvider(new DarkYahooHandler(
            new EdgeFeedHandler(TradeDate, chartFails: true)));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.FetchAsync(TradeDate));

        Assert.Contains("404", error.Message, StringComparison.Ordinal);
        Assert.Contains("PSE Edge", error.Message, StringComparison.Ordinal);
        Assert.Contains("PSEI.PS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchAsync_builds_the_session_from_pse_edge_when_yahoo_has_no_pse_series()
    {
        // The whole reason the fallback exists: with Yahoo dark on every .PS symbol but PSE Edge
        // live, a session still has to be produced from real exchange history.
        var live = new LiveFeedHandler(TradeDate);
        var edge = new EdgeFeedHandler(TradeDate);

        var provider = CreateProvider(new StubHandler((request, token) =>
        {
            var uri = request.RequestUri?.ToString() ?? string.Empty;

            if (uri.Contains("edge.pse.com.ph", StringComparison.Ordinal))
            {
                return edge.RespondAsync(request, token);
            }

            // Yahoo still answers the FX and macro symbols; only the PSE series are dark.
            return uri.Contains(".PS", StringComparison.Ordinal)
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))
                : live.RespondAsync(request, token);
        }));

        var batch = await provider.FetchAsync(TradeDate);

        Assert.True(batch.Stocks.Count >= 10, $"only {batch.Stocks.Count} tickers survived the Edge fallback");
        Assert.All(batch.Stocks, stock => Assert.Equal(TradeDate, stock.LatestBar!.Date));
        Assert.All(batch.Stocks, stock => Assert.True(stock.Bars.Count >= QuantitativeFilterService.MinBarsRequired));

        var indexBars = Assert.IsAssignableFrom<IReadOnlyList<OhlcvBar>>(batch.IndexBars);
        Assert.True(indexBars.Count >= 200);
        Assert.Equal(TradeDate, indexBars[^1].Date);
        Assert.Equal(MarketRegime.BULLISH, batch.MarketRegime);

        // The proxy is still scale-free: a level and the PSEi moving averages stay unreported.
        Assert.Equal(0m, batch.PseiClose);
        Assert.Equal(0m, batch.PseiSma50);
        Assert.Contains("proxy", batch.RegimeNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task FetchAsync_reports_how_short_the_edge_series_is_when_it_cannot_fill_the_window()
    {
        // Edge answering with a real but short series is the one case where "no bars" and "not
        // enough bars" look alike; the diagnostic has to say which, or the blank tail
        // ("PSE Edge: .") tells an operator nothing.
        var provider = CreateProvider(new DarkYahooHandler(new EdgeFeedHandler(TradeDate, bars: 40)));

        var error = await Assert.ThrowsAsync<MarketDataUnavailableException>(
            () => provider.FetchAsync(TradeDate));

        Assert.Contains("40 bar(s), newest", error.Message, StringComparison.Ordinal);
        Assert.Contains(TradeDate.ToString("yyyy-MM-dd"), error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("PSE Edge: .", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablePseEdgeHistory_false_keeps_the_strict_yahoo_only_contract()
    {
        var edge = new EdgeFeedHandler(TradeDate);
        var provider = CreateProvider(
            new DarkYahooHandler(edge),
            options => options.EnablePseEdgeHistory = false);

        var stock = await provider.FetchSingleStockAsync("JFC", TradeDate);

        Assert.Null(stock);
        Assert.Equal(0, edge.RequestCount);
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

        /// <summary>Allows a composing handler to route through this stub.</summary>
        public Task<HttpResponseMessage> SendForTest(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Stands in for the current live feed: every Yahoo chart answers <c>404</c> (Yahoo no longer
    /// publishes PSE equities) while PSE Edge keeps serving.
    /// </summary>
    private sealed class DarkYahooHandler : HttpMessageHandler
    {
        private readonly EdgeFeedHandler _edge;

        public DarkYahooHandler(EdgeFeedHandler edge) => _edge = edge;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            (request.RequestUri?.Host ?? string.Empty).Contains("edge.pse.com.ph", StringComparison.Ordinal)
                ? _edge.RespondAsync(request, cancellationToken)
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>
    /// Answers the three PSE Edge calls the history source makes — the symbol search, the company
    /// page and the chart POST — with a deterministic rising daily series. Every other Edge URL
    /// gets a plain announcements page, so disclosures keep working.
    /// </summary>
    private sealed class EdgeFeedHandler : HttpMessageHandler
    {
        private readonly DateOnly _endDate;
        private readonly int _bars;
        private readonly decimal _start;
        private readonly decimal _step;
        private readonly decimal _valuePerBar;
        private readonly int _duplicateSessions;
        private readonly bool _chartFails;
        private readonly string _securityId;
        private int _requests;

        public EdgeFeedHandler(
            DateOnly endDate,
            int bars = 220,
            decimal start = 100m,
            decimal step = 0.5m,
            decimal valuePerBar = 12_000_000m,
            int duplicateSessions = 0,
            bool chartFails = false,
            string securityId = "158")
        {
            _endDate = endDate;
            _bars = bars;
            _start = start;
            _step = step;
            _valuePerBar = valuePerBar;
            _duplicateSessions = duplicateSessions;
            _chartFails = chartFails;
            _securityId = securityId;
        }

        /// <summary>Every request this stub answered, so a test can prove a feed was never called.</summary>
        public int RequestCount => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            RespondAsync(request, cancellationToken);

        public Task<HttpResponseMessage> RespondAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);

            var uri = request.RequestUri?.ToString() ?? string.Empty;

            if (uri.Contains("searchCompanyNameSymbol.ax", StringComparison.Ordinal))
            {
                var term = ReadQuery(uri, "term");

                return Json(
                    $"[{{\"cmpyId\":\"86\",\"cmpyNm\":\"{term} Holdings\",\"symbol\":\"{term}\",\"etfYn\":\"0\"}}]");
            }

            if (uri.Contains("companyPage/stockData.do", StringComparison.Ordinal))
            {
                return Html(
                    $"<select name=\"security_id\"><option value=\"{_securityId}\" selected>ISSUER</option></select>" +
                    $"<script>sendData.security_id = \"{_securityId}\";</script>");
            }

            if (uri.Contains("common/DisclosureCht.ax", StringComparison.Ordinal))
            {
                if (_chartFails)
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = new StringContent("upstream unavailable", Encoding.UTF8, "text/plain")
                    });
                }

                return Json(BuildChartJson());
            }

            return Html(
                "<html><body><a href=\"#\">SM Investments Corporation declares cash dividend</a></body></html>");
        }

        private string BuildChartJson()
        {
            var rows = new List<string>(_bars + _duplicateSessions);

            for (var offset = _bars - 1; offset >= 0; offset--)
            {
                var close = _start + (_step * (_bars - offset));

                rows.Add(Row(_endDate.AddDays(-offset), close - 0.02m, close + 0.05m, close - 0.05m, close));
            }

            // Edge repeats a session now and then. The repeat lands last and prints a different
            // close, so a test can tell which print survived the de-duplication.
            for (var duplicate = 0; duplicate < _duplicateSessions; duplicate++)
            {
                rows.Add(Row(_endDate.AddDays(-1 - duplicate), 1.4m, 1.6m, 1.3m, 1.5m));
            }

            return "{\"chartData\":[" + string.Join(",", rows) + "]}";
        }

        private string Row(DateOnly date, decimal open, decimal high, decimal low, decimal close) =>
            $"{{\"OPEN\":{Number(open)},\"VALUE\":{Number(_valuePerBar)},\"CLOSE\":{Number(close)}," +
            $"\"CHART_DATE\":\"{date.ToString("MMM d, yyyy", CultureInfo.InvariantCulture)} 00:00:00\"," +
            $"\"HIGH\":{Number(high)},\"LOW\":{Number(low)}}}";

        private static string ReadQuery(string uri, string name)
        {
            var query = new Uri(uri).Query.TrimStart('?');

            foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var pair = part.Split('=', 2);

                if (pair.Length == 2 && pair[0].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return Uri.UnescapeDataString(pair[1]);
                }
            }

            return string.Empty;
        }

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

        private static string Number(decimal value) =>
            value.ToString("0.####", CultureInfo.InvariantCulture);
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
