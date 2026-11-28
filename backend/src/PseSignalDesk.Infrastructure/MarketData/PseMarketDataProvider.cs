using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.Infrastructure.Diagnostics;
using PseSignalDesk.Infrastructure.Scheduling;

namespace PseSignalDesk.Infrastructure.MarketData;

/// <summary>
/// The USD/PHP reading for one session: the close the snapshot stores plus the two macro
/// dials that make it interpretable (50D average and 20-session change). Both dials are
/// nullable because a short FX window cannot support them and a fabricated flat reading
/// would be worse than reporting nothing.
/// </summary>
internal sealed record UsdPhpQuote(decimal Last, decimal? Sma50, decimal? Change20dPct);

/// <summary>
/// Best-effort cross-asset macro read for one session. Every field is nullable: an unavailable
/// Yahoo series leaves that dial unreported instead of failing the PSE run or storing a zero.
/// </summary>
internal sealed record MacroBenchmarks(
    decimal? Us10YClosePct,
    decimal? Us10YChange20dBps,
    decimal? BrentUsd,
    decimal? BrentChange20dPct,
    decimal? EpheCloseUsd,
    decimal? EpheChange20dPct)
{
    public static MacroBenchmarks Empty { get; } = new(null, null, null, null, null, null);
}

/// <summary>
/// Live-only market data. Every candle comes from Yahoo Finance's v8 chart endpoint
/// (<c>PSEI.PS</c>, <c>PHP=X</c> and each tracked <c>{TICKER}.PS</c> symbol) and every
/// disclosure from PSE Edge. Any upstream failure raises
/// <see cref="MarketDataUnavailableException"/> (HTTP 503): there is no synthetic
/// series, no modelled history and no fallback batch.
/// </summary>
public sealed class PseMarketDataProvider : IMarketDataProvider
{
    private const string UsdPhpSymbol = "PHP=X";
    private const int MinimumIndexBars = QuantitativeFilterService.SmaLongPeriod;
    private const int MinimumStockBars = QuantitativeFilterService.SmaLongPeriod;
    private const int MinimumStockCount = 10;
    private const int MinimumProxyConstituents = 6;
    private const int MaxSessionLagDays = 7;
    private const int MacroLookbackDays = 75;
    private const string LiveQuotationSource = "pse-dqr-live";

    /// <summary>
    /// How many per-company disclosure pages are in flight at once. The fan-out is at most one
    /// request per screened name, so the cap is politeness to the exchange rather than a
    /// throughput requirement.
    /// </summary>
    private const int CompanyDisclosureConcurrency = 8;

    /// <summary>
    /// Upstream freshness bounds. A run only needs the tape to be right for as long as it lasts,
    /// and the daily run, a backtest and the research panel all request the same chart URLs, so a
    /// chart body is reused for ten minutes and an Edge page for five. <see cref="InvalidateCache"/>
    /// clears both when a caller needs the newest tape instead.
    /// </summary>
    private static readonly TimeSpan ChartCacheTimeToLive = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan DisclosureCacheTimeToLive = TimeSpan.FromMinutes(5);

    private static readonly Regex AnchorTextRegex = new(
        "<a[^>]*>(?<text>[^<]{15,240})</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TitleAttributeRegex = new(
        "title=\"(?<text>[^\"]{15,240})\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TableRowRegex = new(
        "<tr[^>]*>(?<row>.*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex TableCellRegex = new(
        "<t[dh][^>]*>(?<cell>.*?)</t[dh]>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Page furniture that is a link but never a filing subject.</summary>
    private static readonly Regex NoiseTextRegex = new(
        @"^(?:view|download|print|share|more|read|details?|back|next|previous)\b|\.(?:pdf|html?)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Entity words a headline routinely abbreviates or drops from a company name.</summary>
    private static readonly HashSet<string> LegalSuffixWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc", "incorporated", "corp", "corporation", "co", "company", "holdings", "holding", "group"
    };

    private static readonly Regex FilingDateRegex = new(
        @"(?<m>\d{1,2})/(?<d>\d{1,2})/(?<y>\d{4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The disclosure table prints <c>Nov 26, 2026 05:20 PM</c> instead of <c>11/26/2026</c>. Both
    /// forms have to be readable: a date the parser misses is not merely unparsed, it is kept as a
    /// <em>subject line</em> and the filing is back-dated to the session.
    /// </summary>
    private static readonly Regex AnnounceDateRegex = new(
        @"(?<mon>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+(?<d>\d{1,2}),\s*(?<y>\d{4})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] MonthAbbreviations =
    {
        "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"
    };

    /// <summary>
    /// The short code cells of a disclosure table — <c>13-1</c>, <c>4-23</c>, <c>ETF-1</c>,
    /// <c>DD-5</c>, <c>C08413-2026</c>, <c>DN00191-2026</c>. They are the PSE form and report
    /// numbers that identify a filing, never a subject line, and the live table prints them in the
    /// same row after the subject.
    /// </summary>
    private static readonly Regex FilingIdentifierRegex = new(
        @"^(?:[A-Z]{0,4}\d{0,5}-\d{1,4}(?:-\d{4})?|\d{1,3}-[A-Z])$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly MarketDataOptions _options;
    private readonly ILogger<PseMarketDataProvider> _logger;
    private readonly IDailyQuotationParser _quotationParser;
    private readonly MarketDataCache _cache;

    /// <summary>
    /// What each upstream actually answered during the last run. A silent degradation (Edge
    /// answering "no records found" for the disclosure feed) is invisible in the return value, so
    /// it is recorded here and surfaced by <c>GET /api/health/feeds</c>.
    /// </summary>
    private readonly IFeedHealthRegistry _feedHealth;

    /// <summary>
    /// The index symbols and proxy constituents as they are actually fetched: blank and repeated
    /// entries removed. The configuration binder appends a section onto the code defaults, so a
    /// section that repeats them would otherwise request the same symbol twice and report a
    /// doubled constituent count in the failure message.
    /// </summary>
    private readonly IReadOnlyList<string> _indexSymbols;
    private readonly IReadOnlyList<string> _proxyConstituents;

    /// <summary>
    /// The exchange's own daily history, used when a Yahoo chart carries no series. Null when
    /// <see cref="MarketDataOptions.EnablePseEdgeHistory"/> is off.
    /// </summary>
    private readonly PseEdgeHistorySource? _edgeHistory;

    /// <summary>
    /// The per-company disclosure listing, used to attribute a filing by the exchange's own
    /// <c>cmpy_id</c>. Always constructed — the option that bounds it is read per call, so a limit
    /// of zero disables the work without changing the object graph.
    /// </summary>
    private readonly PseEdgeDisclosureSource _companyDisclosures;

    /// <summary>
    /// The exchange's own dividends table, used to give a PSE name the cash dividends Yahoo no
    /// longer publishes. Null when <see cref="MarketDataOptions.EnablePseEdgeDividends"/> is off.
    /// </summary>
    private readonly PseEdgeDividendSource? _edgeDividends;

    public PseMarketDataProvider(
        HttpClient httpClient,
        IOptions<MarketDataOptions> options,
        ILogger<PseMarketDataProvider> logger,
        IDailyQuotationParser? quotationParser = null,
        MarketDataCache? cache = null,
        IFeedHealthRegistry? feedHealth = null)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
        _quotationParser = quotationParser ?? new PseDailyQuotationParser();
        _cache = cache ?? new MarketDataCache();

        // A hand-built provider (every test, any DI-free caller) gets its own private ledger, so
        // recording never requires the container and never leaks between instances.
        _feedHealth = feedHealth ?? new FeedHealthRegistry();
        _indexSymbols = MarketDataOptions.DistinctNonBlank(_options.IndexYahooSymbols);
        _proxyConstituents = MarketDataOptions.DistinctNonBlank(_options.IndexProxyConstituents);
        _edgeHistory = _options.EnablePseEdgeHistory
            ? new PseEdgeHistorySource(httpClient, _options, _cache, logger, _feedHealth)
            : null;
        _companyDisclosures = new PseEdgeDisclosureSource(httpClient, _options, _cache, logger, _feedHealth);
        _edgeDividends = _options.EnablePseEdgeHistory && _options.EnablePseEdgeDividends
            ? new PseEdgeDividendSource(httpClient, _options, _cache, logger, _feedHealth)
            : null;


    }

    /// <summary>
    /// Drops every cached upstream response so the next fetch reflects the live tape. The
    /// container registers the cache as a singleton, so this clears it for every provider
    /// instance in the process; providers built by hand own their private cache.
    /// </summary>
    public void InvalidateCache() => _cache.Clear();

    public Task<MarketDataBatch> FetchAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken = default) =>
        FetchAsync(tradeDate, lookbackDaysOverride: null, cancellationToken);

    /// <summary>
    /// Fetches the session with an explicit calendar lookback. The walk-forward backtester
    /// asks for roughly two years so its replay has real history, while the daily run keeps
    /// the configured short window and stays fast.
    /// </summary>
    public async Task<MarketDataBatch> FetchAsync(
        DateOnly tradeDate,
        int? lookbackDaysOverride,
        CancellationToken cancellationToken = default)
    {
        // Every counter below describes this run. Process-lifetime sources (embeddings) keep
        // theirs, because they are exercised at indexing time rather than per session.
        _feedHealth.BeginRun();

        var lookbackDays = NormalizeLookback(lookbackDaysOverride);
        var index = await ResolveIndexAsync(tradeDate, lookbackDays, cancellationToken);
        var indexBars = index.Bars;
        var sessionDate = indexBars[^1].Date;

        var (pseiClose, pseiSma50, pseiSma200, regime) = ComputeIndexRegime(index);
        var pseiRealizedVol20dPct = QuantitativeFilterService.ComputeRealizedVolatilityPct(
            indexBars.Select(bar => bar.Close).ToList());

        // Every remaining stage is addressed by the session date the index just decided, and none
        // of them needs another's result, so they run together instead of in a chain: the FX
        // series, the Edge pages, the DQR and the macro dials are downloaded while the ticker
        // charts are already in flight. The stock stage still reads the disclosure map — it
        // attaches each ticker's headlines — but it only awaits that map once its own chart has
        // landed, so a slow Edge page no longer holds up 24 chart downloads.
        var usdPhpTask = FetchUsdPhpAsync(sessionDate, lookbackDays, cancellationToken);
        var disclosuresTask = FetchDisclosuresAsync(sessionDate, cancellationToken);
        var quotationTask = TryFetchQuotationReportAsync(sessionDate, cancellationToken);
        var macroTask = FetchMacroBenchmarksAsync(sessionDate, cancellationToken);
        var stocksTask = FetchStocksAsync(sessionDate, disclosuresTask, lookbackDays, cancellationToken);

        await Task.WhenAll(usdPhpTask, disclosuresTask, quotationTask, macroTask, stocksTask);

        var usdPhp = await usdPhpTask;
        var stocks = await stocksTask;

        // The two best-effort enrichments were part of the same fan-out: the DQR and the macro
        // symbols fail soft to null, and the run keeps its live-only guarantees.
        var quotation = await quotationTask;
        var macro = await macroTask;
        var netForeignFlowPhp = quotation?.MarketNetForeignFlowPhp;
        var foreignFlowSource = netForeignFlowPhp is null ? null : LiveQuotationSource;
        stocks = ApplyQuotationReport(stocks, quotation, sessionDate);

        _logger.LogInformation(
            "Live market data for the {SessionDate} PSE session: PSEi {PseiClose}, {Regime}, USD/PHP {UsdPhp}, " +
            "{Stocks} tickers, market net foreign {NetForeign}, US10Y {Us10Y}, DQR {Dqr}.",
            sessionDate,
            pseiClose,
            regime,
            usdPhp.Last,
            stocks.Count,
            netForeignFlowPhp?.ToString("0.##") ?? "unreported",
            macro.Us10YClosePct?.ToString("0.###") ?? "unreported",
            quotation is null ? "unavailable" : "parsed");

        return new MarketDataBatch(
            // The snapshot is dated by the real session (the newest PSEI bar), never by the
            // calendar day that was requested, so a weekend/holiday run cannot create a
            // phantom trading day in the drift chain.
            TradeDate: sessionDate,
            PseiClose: pseiClose,
            PseiSma50: pseiSma50,
            PseiSma200: pseiSma200,
            NetForeignFlowPhp: netForeignFlowPhp,
            UsdPhp: usdPhp.Last,
            MarketRegime: regime,
            RegimeNote: BuildRegimeNote(
                pseiClose,
                pseiSma50,
                pseiSma200,
                usdPhp,
                pseiRealizedVol20dPct,
                netForeignFlowPhp,
                foreignFlowSource,
                index,
                macro),
            Stocks: stocks,
            Source: quotation is null ? "yahoo-finance+pse-edge" : "yahoo-finance+pse-edge+pse-dqr",
            IndexBars: indexBars,
            UsdPhpSma50: usdPhp.Sma50,
            UsdPhpChange20dPct: usdPhp.Change20dPct,
            PseiRealizedVol20dPct: pseiRealizedVol20dPct,
            Us10YClosePct: macro.Us10YClosePct,
            Us10YChange20dBps: macro.Us10YChange20dBps,
            BrentUsd: macro.BrentUsd,
            BrentChange20dPct: macro.BrentChange20dPct,
            EpheCloseUsd: macro.EpheCloseUsd,
            EpheChange20dPct: macro.EpheChange20dPct,
            ForeignFlowSource: foreignFlowSource);
    }

    /// <summary>
    /// Whether the symbol is part of the tracked catalog. The research endpoint uses it to
    /// label a name as "researched on demand" instead of guessing from the data source.
    /// </summary>
    public bool IsTracked(string symbol) =>
        PseTickerCatalog.Find((symbol ?? string.Empty).Trim()) is not null;

    /// <summary>
    /// The tracked catalog as plain metadata, so the terminal can search by company name and
    /// the research endpoint can resolve "Jollibee" to JFC without ever asking the model.
    /// </summary>
    public IReadOnlyList<CatalogStockDto> GetCatalog() =>
        PseTickerCatalog.All
            .Select(item => new CatalogStockDto(item.Ticker, item.CompanyName, item.Sector))
            .ToList();

    /// <summary>
    /// The PSEi series for single-name research. It uses the configured daily lookback: the
    /// factors it feeds (63-session relative strength, 252-session beta) are computed from
    /// whatever history the feed returns, and a short series simply reports them as null.
    /// </summary>
    public async Task<IReadOnlyList<OhlcvBar>> FetchIndexBarsAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken = default)
    {
        var index = await ResolveIndexAsync(tradeDate, NormalizeLookback(null), cancellationToken);

        return index.Bars;
    }

    /// <summary>
    /// On-demand research for one symbol. It accepts any <c>{TICKER}.PS</c>, not only the
    /// tracked catalog, and returns null when Yahoo has no usable 200-session series. PSE
    /// Edge headlines are best-effort here: a scrape failure never blocks the research.
    /// </summary>
    public async Task<StockMarketData?> FetchSingleStockAsync(
        string symbol,
        DateOnly tradeDate,
        CancellationToken cancellationToken = default)
    {
        var ticker = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (ticker.Length == 0)
        {
            return null;
        }

        var known = PseTickerCatalog.Find(ticker);
        var yahooSymbol = known?.YahooSymbol ?? $"{ticker}.PS";
        var chart = await FetchChartAsync(
            yahooSymbol,
            tradeDate,
            _options.LookbackCalendarDays,
            cancellationToken,
            dropZeroVolume: true);

        chart = await WithEdgeHistoryAsync(
            chart,
            ticker,
            tradeDate,
            _options.LookbackCalendarDays,
            MinimumStockBars,
            cancellationToken);

        chart = await WithEdgeDividendsAsync(chart, ticker, tradeDate, cancellationToken);

        if (chart.SymbolUnavailable || chart.Bars.Count < MinimumStockBars)
        {
            _logger.LogInformation(
                "No usable research series for {Ticker} ({Symbol}): {Reason}.",
                ticker,
                yahooSymbol,
                (chart.SymbolUnavailable ? chart.UnavailableReason ?? "no chart" : $"{chart.Bars.Count} bars")
                    .TrimEnd('.'));

            return null;
        }

        var sessionDate = chart.Bars[^1].Date;
        var companyName = known?.CompanyName ?? ticker;
        var sector = known?.Sector ?? "PSE";
        IReadOnlyList<StockDisclosure> disclosures;

        try
        {
            var html = await FetchDisclosureHtmlAsync(cancellationToken);
            var filings = ExtractFilings(html, sessionDate);

            if (known is null)
            {
                // An untracked symbol has no company phrase to match on, so only an exact ticker
                // token can be trusted; anything looser would attach another issuer's filing.
                disclosures = filings
                    .Where(filing => ContainsToken(filing.Headline, ticker))
                    .OrderByDescending(filing => filing.Date)
                    .Take(3)
                    .Select(filing => new StockDisclosure(filing.Date, filing.Headline, "Disclosure"))
                    .ToList();
            }
            else
            {
                disclosures = AttributeFilings(filings).TryGetValue(known.Ticker, out var attributed)
                    ? attributed
                        .OrderByDescending(filing => filing.Date)
                        .Take(3)
                        .Select(filing => new StockDisclosure(filing.Date, filing.Headline, "Disclosure"))
                        .ToList()
                    : Array.Empty<StockDisclosure>();
            }
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning(ex, "PSE Edge disclosures unavailable for the {Ticker} research fetch.", ticker);
            disclosures = Array.Empty<StockDisclosure>();
        }

        return new StockMarketData(
            ticker,
            companyName,
            sector,
            chart.Bars,
            NetForeignTodayPhp: null,
            disclosures,
            chart.Dividends);
    }

    /// <summary>
    /// Reads the three cross-asset series concurrently. Each is best-effort: a missing series
    /// leaves that dial null and the PSE run continues, because a macro outage must not hide the
    /// live tape.
    /// </summary>
    private async Task<MacroBenchmarks> FetchMacroBenchmarksAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        var us10YTask = TryFetchMacroSeriesAsync(_options.Us10YahooSymbol, sessionDate, cancellationToken);
        var brentTask = TryFetchMacroSeriesAsync(_options.BrentYahooSymbol, sessionDate, cancellationToken);
        var epheTask = TryFetchMacroSeriesAsync(_options.EpheYahooSymbol, sessionDate, cancellationToken);

        await Task.WhenAll(us10YTask, brentTask, epheTask);

        var us10Y = ReadMacroValue(await us10YTask, sessionDate, decimals: 3, changeInBasisPoints: true);
        var brent = ReadMacroValue(await brentTask, sessionDate, decimals: 2);
        var ephe = ReadMacroValue(await epheTask, sessionDate, decimals: 2);

        // The dials are context, not signals: a missing one is reported, never fabricated. The
        // health ledger says how many of the three survived, so a permanently absent symbol is
        // visible instead of being read as "no macro risk today".
        var reported = new[] { us10Y.Close, brent.Close, ephe.Close }.Count(close => close is not null);

        if (reported == 0)
        {
            _feedHealth.RecordFailure(
                FeedSource.MacroDials,
                "none of the three cross-asset dials (US 10Y, Brent, EPHE) reported.");
        }
        else if (reported < 3)
        {
            _feedHealth.RecordDegraded(
                FeedSource.MacroDials,
                $"{reported} of 3 cross-asset dials reported; the rest stay unreported.",
                reported);
        }
        else
        {
            _feedHealth.RecordSuccess(FeedSource.MacroDials, "3 of 3 cross-asset dials reported.", reported);
        }

        return new MacroBenchmarks(
            us10Y.Close,
            us10Y.Change,
            brent.Close,
            brent.Change,
            ephe.Close,
            ephe.Change);
    }

    private async Task<IReadOnlyList<OhlcvBar>?> TryFetchMacroSeriesAsync(
        string symbol,
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return null;
        }

        try
        {
            var chart = await FetchChartAsync(
                symbol,
                sessionDate,
                MacroLookbackDays,
                cancellationToken,
                roundToTick: false);

            if (chart.SymbolUnavailable || chart.Bars.Count == 0)
            {
                _logger.LogWarning(
                    "Macro feed {Symbol} returned no usable bars ({Reason}); the dial stays unreported.",
                    symbol,
                    chart.UnavailableReason ?? "empty series");
                return null;
            }

            return chart.Bars;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Macro feed {Symbol} unavailable; the dial stays unreported.", symbol);
            return null;
        }
    }

    /// <summary>
    /// Latest close plus its 20-session change for a macro series. The close must be within a week
    /// of the PSE session; a stale or short series reports null instead of a fabricated level.
    /// Yield changes are expressed in basis points, everything else in percent.
    /// </summary>
    private static (decimal? Close, decimal? Change) ReadMacroValue(
        IReadOnlyList<OhlcvBar>? bars,
        DateOnly sessionDate,
        int decimals,
        bool changeInBasisPoints = false)
    {
        if (bars is null || bars.Count == 0)
        {
            return (null, null);
        }

        var last = bars[^1];

        if (last.Close <= 0m ||
            last.Date > sessionDate ||
            last.Date < sessionDate.AddDays(-MaxSessionLagDays))
        {
            return (null, null);
        }

        var window = bars.Where(bar => bar.Date <= last.Date).ToList();
        decimal? change = null;
        var period = QuantitativeFilterService.MoneyFlowPeriod;

        if (window.Count > period)
        {
            var reference = window[^(period + 1)].Close;

            if (reference > 0m)
            {
                change = changeInBasisPoints
                    ? decimal.Round((last.Close - reference) * 100m, 1, MidpointRounding.AwayFromZero)
                    : decimal.Round((last.Close - reference) / reference * 100m, 2, MidpointRounding.AwayFromZero);
            }
        }

        return (decimal.Round(last.Close, decimals, MidpointRounding.AwayFromZero), change);
    }

    /// <summary>
    /// Tries each configured date-addressed DQR source in order. This is best-effort in every
    /// direction: no templates configured, a 404, a non-2xx status, an unparseable file or a
    /// timeout all leave foreign flow unreported (null), and the live tape still runs.
    /// </summary>
    private async Task<ParsedDailyQuotationReport?> TryFetchQuotationReportAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        if (_options.PseDailyQuotationUrlTemplates.Count == 0)
        {
            return null;
        }

        foreach (var template in _options.PseDailyQuotationUrlTemplates)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            var expanded = ExpandQuotationReportUrl(template, sessionDate);

            if (!Uri.TryCreate(expanded, UriKind.Absolute, out var uri))
            {
                _logger.LogWarning("Ignoring malformed PSE DQR template: {Template}", template);
                continue;
            }

            try
            {
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

                using var response = await _httpClient.GetAsync(uri, requestTimeout.Token);

                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                {
                    _feedHealth.RecordSkipped(
                        FeedSource.QuotationReport,
                        $"{sessionDate:yyyy-MM-dd}: no report published at {(int)response.StatusCode} for the configured template.");

                    _logger.LogInformation(
                        "No PSE Daily Quotation Report at {Url} (HTTP {Status}).",
                        uri,
                        (int)response.StatusCode);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _feedHealth.RecordFailure(
                        FeedSource.QuotationReport,
                        $"{sessionDate:yyyy-MM-dd}: HTTP {(int)response.StatusCode}.");

                    _logger.LogWarning(
                        "PSE Daily Quotation Report at {Url} returned HTTP {Status}; foreign flow stays unreported.",
                        uri,
                        (int)response.StatusCode);
                    continue;
                }

                var content = await response.Content.ReadAsByteArrayAsync(requestTimeout.Token);
                var report = _quotationParser.Parse(uri.AbsolutePath, content);

                if (!report.HasData)
                {
                    _feedHealth.RecordDegraded(
                        FeedSource.QuotationReport,
                        $"{sessionDate:yyyy-MM-dd}: the report answered but contained no recognizable foreign-flow rows.");

                    _logger.LogWarning(
                        "PSE Daily Quotation Report at {Url} contained no recognizable foreign-flow rows.",
                        uri);
                    continue;
                }

                _feedHealth.RecordSuccess(
                    FeedSource.QuotationReport,
                    $"{sessionDate:yyyy-MM-dd}: market net foreign {(report.MarketNetForeignFlowPhp is decimal flow ? flow.ToString("0.##", CultureInfo.InvariantCulture) : "unreported")}, {report.ByTicker.Count} ticker(s).",
                    report.ByTicker.Count);

                _logger.LogInformation(
                    "Parsed the official PSE Daily Quotation Report for {Date}: market net foreign {Flow}, " +
                    "{Tickers} tickers, {Suspended} suspended.",
                    sessionDate,
                    report.MarketNetForeignFlowPhp?.ToString("0.##") ?? "unreported",
                    report.ByTicker.Count,
                    report.SuspendedTickers.Count);

                return report;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _feedHealth.RecordFailure(
                    FeedSource.QuotationReport,
                    $"{sessionDate:yyyy-MM-dd}: {ex.GetType().Name} while reading the report.");

                _logger.LogWarning(ex, "PSE Daily Quotation Report fetch failed for {Url}.", uri);
            }
        }

        return null;
    }

    private static string ExpandQuotationReportUrl(string template, DateOnly sessionDate) =>
        template
            .Replace("{yyyy}", sessionDate.ToString("yyyy", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{MMddyyyy}", sessionDate.ToString("MMddyyyy", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{MMddyy}", sessionDate.ToString("MMddyy", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Overlays the official report onto the fetched stocks: per-ticker net foreign flow plus a
    /// synthetic halt disclosure for suspended names. A ticker the report does not mention keeps
    /// its previous value, which is null (unreported) in the live path.
    /// </summary>
    private static IReadOnlyList<StockMarketData> ApplyQuotationReport(
        IReadOnlyList<StockMarketData> stocks,
        ParsedDailyQuotationReport? report,
        DateOnly sessionDate)
    {
        if (report is null)
        {
            return stocks;
        }

        var suspended = new HashSet<string>(report.SuspendedTickers, StringComparer.OrdinalIgnoreCase);
        var updated = new List<StockMarketData>(stocks.Count);

        foreach (var stock in stocks)
        {
            var changed = false;
            var netForeign = stock.NetForeignTodayPhp;

            if (report.ByTicker.TryGetValue(stock.Ticker, out var quote))
            {
                netForeign = decimal.Round(quote.NetForeignPhp, 2, MidpointRounding.AwayFromZero);
                changed = netForeign != stock.NetForeignTodayPhp;
            }

            var disclosures = stock.Disclosures;

            if (suspended.Contains(stock.Ticker))
            {
                var alreadyRecorded = disclosures.Any(disclosure =>
                    disclosure.Date == sessionDate &&
                    PseDisclosureClassifier.Classify(disclosure.Headline).Category == DisclosureCategory.TRADING_HALT);

                if (!alreadyRecorded)
                {
                    disclosures = disclosures
                        .Concat(new[]
                        {
                            new StockDisclosure(
                                sessionDate,
                                $"{stock.Ticker} was listed as suspended in the official PSE Daily Quotation Report",
                                "Disclosure",
                                "PSE DQR")
                        })
                        .ToList();

                    changed = true;
                }
            }

            updated.Add(changed
                ? stock with { NetForeignTodayPhp = netForeign, Disclosures = disclosures }
                : stock);
        }

        return updated;
    }

    private int NormalizeLookback(int? lookbackDaysOverride) =>
        Math.Max(MinimumIndexBars + 60, lookbackDaysOverride ?? _options.LookbackCalendarDays);


    /// <summary>
    /// Resolves the market-trend series for the session. The configured index symbols are tried
    /// in order; Yahoo currently answers <c>PSEI.PS</c> with a single live bar and no history, so
    /// when none of them carries a usable series the trend is derived from an equal-weight proxy
    /// of real heavyweight closes. The proxy keeps the regime, breadth and realized volatility
    /// honest (all of them are scale-free) and never supplies a fake index level: the level comes
    /// from the index quote itself, and the index moving averages stay unreported.
    /// </summary>
    private async Task<IndexSeries> ResolveIndexAsync(
        DateOnly tradeDate,
        int lookbackDays,
        CancellationToken cancellationToken)
    {
        var reasons = new List<string>();
        var quotes = new List<(DateOnly? Date, decimal Price)>();

        foreach (var symbol in _indexSymbols)
        {
            // The index is date-bearing for the whole run, so a flat zero-volume echo row for a
            // session that never traded must be dropped here as well as on the stocks.
            var chart = await FetchChartAsync(
                symbol,
                tradeDate,
                lookbackDays,
                cancellationToken,
                dropZeroVolume: true);

            if (chart is { LivePrice: decimal livePrice, LiveDate: DateOnly liveDate })
            {
                quotes.Add((liveDate, livePrice));
            }

            if (chart.SymbolUnavailable || chart.Bars.Count < MinimumIndexBars)
            {
                reasons.Add($"{symbol}: " +
                            (chart.SymbolUnavailable
                                ? chart.UnavailableReason
                                : $"{chart.Bars.Count} bar(s), {MinimumIndexBars} required"));

                _logger.LogWarning(
                    "Index symbol {Symbol} carries no usable history for the {Date} session ({Reason}).",
                    symbol,
                    tradeDate,
                    reasons[^1]);

                continue;
            }

            var sessionDate = chart.Bars[^1].Date;
            EnsureSessionIsRecent(sessionDate, tradeDate, $"PSEi ({symbol})");

            return new IndexSeries(chart.Bars, IsProxy: false, LiveClose: null, ProxyConstituentCount: 0);
        }

        var constituentBars = await FetchProxyConstituentBarsAsync(tradeDate, lookbackDays, reasons, cancellationToken);

        if (constituentBars.Count < MinimumProxyConstituents)
        {
            throw new MarketDataUnavailableException(
                "No PSEi index series is published by the live feed and the equal-weight heavyweight proxy " +
                $"could only use {constituentBars.Count} of {_proxyConstituents.Count} constituents " +
                $"({MinimumProxyConstituents} required): {JoinReasons(reasons)}. The live market feed is unavailable.");
        }

        var proxyBars = BuildConstituentProxy(constituentBars);

        if (proxyBars.Count < MinimumIndexBars)
        {
            throw new MarketDataUnavailableException(
                $"The heavyweight proxy produced only {proxyBars.Count} sessions through {tradeDate:yyyy-MM-dd}; " +
                $"{MinimumIndexBars} are required for the 200-day regime.");
        }

        var proxySession = proxyBars[^1].Date;
        EnsureSessionIsRecent(proxySession, tradeDate, "heavyweight proxy");

        // The level is only ever taken from the exchange quote itself, and only when that quote is
        // for the session being recorded; an older quote is not today's close.
        decimal? level = quotes
            .Where(quote => quote.Date == proxySession)
            .Select(quote => (decimal?)quote.Price)
            .FirstOrDefault();

        _logger.LogWarning(
            "The PSEi index has no live history; reading the {Date} trend from an equal-weight proxy of " +
            "{Count} PSE heavyweights. Index level {Level}, index moving averages unreported.",
            proxySession,
            constituentBars.Count,
            level?.ToString("0.##") ?? "unreported");

        return new IndexSeries(proxyBars, IsProxy: true, level, constituentBars.Count);
    }

    private async Task<List<IReadOnlyList<OhlcvBar>>> FetchProxyConstituentBarsAsync(
        DateOnly tradeDate,
        int lookbackDays,
        ICollection<string> reasons,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        var tasks = _proxyConstituents
            .Select(async ticker =>
            {
                var info = PseTickerCatalog.Find(ticker);

                if (info is null)
                {
                    return new ProxyConstituentFetch(null, $"{ticker}: not in the ticker catalog");
                }

                await throttle.WaitAsync(cancellationToken);

                try
                {
                    var chart = await FetchChartAsync(
                        info.YahooSymbol,
                        tradeDate,
                        lookbackDays,
                        cancellationToken,
                        dropZeroVolume: true);

                    chart = await WithEdgeHistoryAsync(
                        chart,
                        info.Ticker,
                        tradeDate,
                        lookbackDays,
                        MinimumIndexBars,
                        cancellationToken);

                    if (chart.SymbolUnavailable || chart.Bars.Count < MinimumIndexBars)
                    {
                        return new ProxyConstituentFetch(
                            null,
                            $"{info.Ticker}: " +
                            (chart.SymbolUnavailable
                                ? chart.UnavailableReason
                                : $"{chart.Bars.Count} bar(s)"));
                    }

                    return new ProxyConstituentFetch(chart.Bars, null);
                }
                finally
                {
                    throttle.Release();
                }
            })
            .ToList();

        var results = await Task.WhenAll(tasks);

        // Each constituent reports its own reason and they are collected after the fan-out, in the
        // configured order: the message a failed run prints is deterministic, and no two tasks
        // append to the same List<T>.
        foreach (var result in results)
        {
            if (result.Reason is not null)
            {
                reasons.Add(result.Reason);
            }
        }

        return results
            .Where(result => result.Bars is not null)
            .Select(result => result.Bars!)
            .ToList();
    }

    /// <summary>
    /// Joins the per-symbol reasons into one sentence fragment. Each reason already ends with a
    /// period, so the trailing period is trimmed before the message adds its own.
    /// </summary>
    private static string JoinReasons(IEnumerable<string> reasons) =>
        string.Join("; ", reasons.Select(reason => reason.TrimEnd('.', ' ')));

    /// <summary>
    /// Builds an equal-weight daily return index from the constituents' real closes: every session
    /// averages the constituent returns that exist for it, and the average is chained onto the
    /// index level. Sessions where fewer than half the constituents traded are skipped rather than
    /// carried forward, so a single stale name cannot flatten the trend.
    /// </summary>
    private static IReadOnlyList<OhlcvBar> BuildConstituentProxy(
        IReadOnlyList<IReadOnlyList<OhlcvBar>> constituentBars)
    {
        var returnsByDate = new SortedDictionary<DateOnly, List<decimal>>();

        foreach (var bars in constituentBars)
        {
            for (var index = 1; index < bars.Count; index++)
            {
                var previousClose = bars[index - 1].Close;
                var close = bars[index].Close;

                if (previousClose <= 0m || close <= 0m)
                {
                    continue;
                }

                if (!returnsByDate.TryGetValue(bars[index].Date, out var returns))
                {
                    returns = new List<decimal>(constituentBars.Count);
                    returnsByDate[bars[index].Date] = returns;
                }

                returns.Add(close / previousClose - 1m);
            }
        }

        var minimumParticipants = Math.Max(3, constituentBars.Count / 2);
        var proxyBars = new List<OhlcvBar>(returnsByDate.Count);
        var level = 100m;

        foreach (var (date, returns) in returnsByDate)
        {
            if (returns.Count < minimumParticipants)
            {
                continue;
            }

            level *= 1m + (returns.Sum() / returns.Count);
            proxyBars.Add(new OhlcvBar(date, level, level, level, level, 0L));
        }

        return proxyBars;
    }

    private static void EnsureSessionIsRecent(DateOnly sessionDate, DateOnly tradeDate, string source)
    {
        if (sessionDate < tradeDate.AddDays(-MaxSessionLagDays))
        {
            throw new MarketDataUnavailableException(
                $"The latest live {source} bar is {sessionDate:yyyy-MM-dd}, more than {MaxSessionLagDays} days before the " +
                $"requested {tradeDate:yyyy-MM-dd}. The exchange session is not available.");
        }
    }

    private static MarketRegime ComputeRegime(IReadOnlyList<OhlcvBar> indexBars)
    {
        var closes = indexBars.Select(bar => bar.Close).ToList();
        var sma50 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaShortPeriod);
        var sma200 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaLongPeriod);

        if (sma50 is null || sma200 is null)
        {
            throw new MarketDataUnavailableException(
                $"The market series returned {closes.Count} bars; the 50D and 200D averages cannot be computed.");
        }

        var close = closes[^1];

        return close >= sma50.Value && close >= sma200.Value
            ? MarketRegime.BULLISH
            : close < sma50.Value && close < sma200.Value
                ? MarketRegime.BEARISH
                : MarketRegime.NEUTRAL;
    }

    /// <summary>
    /// The reported index reading. A real index series reports its close and both averages; the
    /// heavyweight proxy reports the live index quote as the level and 0 for both averages, which
    /// downstream code already reads as "unknown" instead of printing a rebased number as a PSEi
    /// moving average.
    /// </summary>
    private static (decimal Close, decimal Sma50, decimal Sma200, MarketRegime Regime) ComputeIndexRegime(
        IndexSeries index)
    {
        if (index.IsProxy)
        {
            return (
                index.LiveClose is decimal level
                    ? decimal.Round(level, 2, MidpointRounding.AwayFromZero)
                    : 0m,
                0m,
                0m,
                ComputeRegime(index.Bars));
        }

        var closes = index.Bars.Select(bar => bar.Close).ToList();
        var sma50 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaShortPeriod);
        var sma200 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaLongPeriod);

        if (sma50 is null || sma200 is null)
        {
            throw new MarketDataUnavailableException(
                $"PSEi returned {closes.Count} bars; the 50D and 200D averages cannot be computed.");
        }

        return (
            decimal.Round(closes[^1], 2, MidpointRounding.AwayFromZero),
            decimal.Round(sma50.Value, 2, MidpointRounding.AwayFromZero),
            decimal.Round(sma200.Value, 2, MidpointRounding.AwayFromZero),
            ComputeRegime(index.Bars));
    }

    /// <summary>
    /// USD/PHP must have a real close within a week of the PSE session; a missing FX
    /// series is a feed failure, not a reason to assume an exchange rate. The quote also
    /// carries its 50D average and 20-session change, because peso weakness is the macro
    /// dial that decides whether foreign money rotates into PH equities at all.
    /// </summary>
    private async Task<UsdPhpQuote> FetchUsdPhpAsync(
        DateOnly sessionDate,
        int lookbackDays,
        CancellationToken cancellationToken)
    {
        var chart = await FetchChartAsync(UsdPhpSymbol, sessionDate, lookbackDays, cancellationToken);

        var fxBar = chart.SymbolUnavailable
            ? null
            : chart.Bars.LastOrDefault(bar =>
                bar.Date <= sessionDate && bar.Date >= sessionDate.AddDays(-MaxSessionLagDays));

        if (fxBar is null)
        {
            throw new MarketDataUnavailableException(
                $"USD/PHP ({UsdPhpSymbol}) has no live close within {MaxSessionLagDays} days of the " +
                $"{sessionDate:yyyy-MM-dd} PSE session.");
        }

        var closes = chart.Bars
            .Where(bar => bar.Date <= fxBar.Date)
            .Select(bar => bar.Close)
            .ToList();

        var sma50 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaShortPeriod);

        // The 20-session change is the peso's short-term direction; without 21 closes it is
        // unreported rather than assumed flat.
        decimal? change20dPct = null;
        var changePeriod = QuantitativeFilterService.MoneyFlowPeriod;
        if (closes.Count > changePeriod)
        {
            var reference = closes[^(changePeriod + 1)];
            if (reference > 0m)
            {
                change20dPct = decimal.Round(
                    (closes[^1] - reference) / reference * 100m,
                    2,
                    MidpointRounding.AwayFromZero);
            }
        }

        return new UsdPhpQuote(
            decimal.Round(fxBar.Close, 4, MidpointRounding.AwayFromZero),
            sma50 is decimal average ? decimal.Round(average, 4, MidpointRounding.AwayFromZero) : null,
            change20dPct);
    }

    /// <summary>
    /// Reads each screened name's disclosures from the exchange's <b>per-company</b> listing, so
    /// the attribution is the exchange's own <c>cmpy_id</c> rather than a headline match.
    /// <para>
    /// The market-wide page prints subject, date and form numbers with no company column, which
    /// leaves a template like "Mergers and Consolidations" attributable to nobody and the
    /// structural trap screen silently unenforced. This pass closes that gap for the names that
    /// could actually reach the book — at most
    /// <see cref="MarketDataOptions.DisclosureCompanyFanoutLimit"/> of them — and costs one GET
    /// each, eight at a time. Every name that comes back empty keeps whatever the market-wide
    /// scrape attributed, so the two paths compose instead of replacing one another.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>> FetchCompanyDisclosuresAsync(
        IReadOnlyList<string> tickers,
        DateOnly sessionDate,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<StockDisclosure>>(StringComparer.OrdinalIgnoreCase);
        var limit = Math.Max(0, _options.DisclosureCompanyFanoutLimit);

        // A limit of zero is the documented rollback: the run keeps the market-wide scrape alone.
        var targets = limit == 0
            ? new List<string>()
            : MarketDataOptions.DistinctNonBlank(tickers).Take(limit).ToList();

        if (targets.Count == 0)
        {
            return result;
        }

        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = CompanyDisclosureConcurrency,
                CancellationToken = cancellationToken
            },
            async (ticker, token) =>
            {
                var filings = await _companyDisclosures.FetchAsync(ticker, sessionDate, token);

                if (filings.Count == 0)
                {
                    return;
                }

                // A fan-out writes from eight threads; the dictionary is the only shared state.
                lock (result)
                {
                    result[ticker] = filings;
                }
            });

        if (result.Count > 0)
        {
            _feedHealth.RecordSuccess(
                FeedSource.PseEdgeDisclosures,
                $"{sessionDate:yyyy-MM-dd}: {result.Count} of {targets.Count} screened name(s) filed " +
                "through their own Edge page.",
                result.Count);
        }

        return result;
    }

    /// <summary>
    /// Scrapes PSE Edge announcements across the configured pages and both listings. The first
    /// announcement page is the live contract: a transport error, timeout, non-2xx status or an
    /// empty page is fatal (503). Extra pages and the company-disclosure listing are best-effort,
    /// because one successful page with no matching headline already means "no filing today".
    /// </summary>
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>> FetchDisclosuresAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        var filings = await FetchFilingsAsync(sessionDate, cancellationToken);
        var result = new Dictionary<string, IReadOnlyList<StockDisclosure>>(StringComparer.OrdinalIgnoreCase);

        if (filings.Count == 0)
        {
            // PSE Edge answers 200 with a "no records found" page, so this is not a transport
            // failure — it is the silent state that leaves the trap screen unenforced. Recording
            // it as degraded is what makes the desk report it instead of looking healthy.
            _feedHealth.RecordDegraded(
                FeedSource.PseEdgeDisclosures,
                $"{sessionDate:yyyy-MM-dd}: PSE Edge answered with no filing rows.");
            return result;
        }

        var attributed = AttributeFilings(filings);

        foreach (var (ticker, tickerFilings) in attributed)
        {
            var matches = tickerFilings
                .GroupBy(filing => filing.Headline, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(filing => filing.Date).First())
                .OrderByDescending(filing => filing.Date)
                .Take(3)
                .Select(filing => new StockDisclosure(filing.Date, filing.Headline, "Disclosure"))
                .ToList();

            if (matches.Count > 0)
            {
                result[ticker] = matches;
            }
        }

        if (result.Count == 0)
        {
            _feedHealth.RecordDegraded(
                FeedSource.PseEdgeDisclosures,
                $"{sessionDate:yyyy-MM-dd}: {filings.Count} filing(s) read, none attributable to a tracked ticker.");
        }
        else
        {
            _feedHealth.RecordSuccess(
                FeedSource.PseEdgeDisclosures,
                $"{sessionDate:yyyy-MM-dd}: {filings.Count} filing(s) across {result.Count} tracked name(s).",
                filings.Count);
        }

        return result;
    }

    /// <summary>
    /// Attributes every filing to the one catalog name it belongs to. A headline is scored against
    /// the whole catalog and only the strongest candidates keep it, so a phrase match
    /// ("SM Prime Holdings…") beats a bare ticker token ("SM"), and two names that only share a
    /// generic word never both claim the same disclosure.
    /// </summary>
    private static Dictionary<string, List<RawFiling>> AttributeFilings(IReadOnlyList<RawFiling> filings)
    {
        var byTicker = new Dictionary<string, List<RawFiling>>(StringComparer.OrdinalIgnoreCase);

        foreach (var filing in filings)
        {
            var scored = new List<(PseTickerInfo Info, CompanyMatch Match)>();

            foreach (var info in PseTickerCatalog.All)
            {
                var match = MatchStrength(filing.Headline, info);

                if (match.Tier > 0)
                {
                    scored.Add((info, match));
                }
            }

            if (scored.Count == 0)
            {
                continue;
            }

            var best = scored.Max(entry => entry.Match);

            foreach (var (info, _) in scored.Where(entry => entry.Match == best))
            {
                if (!byTicker.TryGetValue(info.Ticker, out var tickerFilings))
                {
                    tickerFilings = new List<RawFiling>();
                    byTicker[info.Ticker] = tickerFilings;
                }

                tickerFilings.Add(filing);
            }
        }

        return byTicker;
    }

    private async Task<IReadOnlyList<RawFiling>> FetchFilingsAsync(
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        var filings = new List<RawFiling>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = Math.Max(1, _options.DisclosurePagesToScan);

        var firstPage = await FetchDisclosureHtmlAsync(_options.PseEdgeAnnouncementsUrl, cancellationToken);
        AddFilings(filings, seen, ExtractFilings(firstPage, sessionDate));

        for (var page = 2; page <= pages; page++)
        {
            var html = await TryFetchDisclosureHtmlAsync(BuildPagedUrl(_options.PseEdgeAnnouncementsUrl, page), cancellationToken);
            if (html is null)
            {
                break;
            }

            var pageFilings = ExtractFilings(html, sessionDate);
            if (pageFilings.Count == 0)
            {
                break;
            }

            AddFilings(filings, seen, pageFilings);
        }

        if (!string.IsNullOrWhiteSpace(_options.PseEdgeCompanyDisclosuresUrl))
        {
            for (var page = 1; page <= pages; page++)
            {
                var html = await TryFetchDisclosureHtmlAsync(
                    BuildPagedUrl(_options.PseEdgeCompanyDisclosuresUrl, page),
                    cancellationToken);

                if (html is null)
                {
                    break;
                }

                var pageFilings = ExtractFilings(html, sessionDate);
                if (pageFilings.Count == 0)
                {
                    break;
                }

                AddFilings(filings, seen, pageFilings);
            }
        }

        return filings;
    }

    private static void AddFilings(
        ICollection<RawFiling> filings,
        ISet<string> seen,
        IReadOnlyList<RawFiling> incoming)
    {
        foreach (var filing in incoming)
        {
            if (seen.Add(filing.Headline))
            {
                filings.Add(filing);
            }
        }
    }

    private static string BuildPagedUrl(string baseUrl, int page) =>
        page <= 1
            ? baseUrl
            : $"{baseUrl}{(baseUrl.Contains('?') ? '&' : '?')}pageNo={page}";

    /// <summary>
    /// Fetches an extra Edge page. The page is served from the cache when it was already
    /// downloaded inside the TTL; an unavailable page is logged and reported as null so the
    /// caller stops paging instead of failing the whole disclosure scan.
    /// </summary>
    private async Task<string?> TryFetchDisclosureHtmlAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            return await FetchDisclosureHtmlAsync(url, cancellationToken);
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning(ex, "Additional PSE Edge disclosure page unavailable: {Url}", url);
            return null;
        }
    }

    private Task<string> FetchDisclosureHtmlAsync(CancellationToken cancellationToken) =>
        FetchDisclosureHtmlAsync(_options.PseEdgeAnnouncementsUrl, cancellationToken);

    /// <summary>
    /// One Edge page, cached per URL for <see cref="DisclosureCacheTimeToLive"/>. Only a downloaded
    /// page is cached: <see cref="DownloadDisclosureHtmlAsync"/> throws on every failure, and the
    /// cache drops a failed entry before the exception reaches the caller, so a transient 5xx is
    /// retried by the next run rather than remembered as an empty page.
    /// </summary>
    private Task<string> FetchDisclosureHtmlAsync(string url, CancellationToken cancellationToken) =>
        _cache.GetOrAddAsync(
            $"edge:{url}",
            DisclosureCacheTimeToLive,
            // The shared download outlives this caller; DownloadDisclosureHtmlAsync still applies
            // the configured per-request timeout on its own.
            () => DownloadDisclosureHtmlAsync(url, CancellationToken.None),
            cancellationToken);

    /// <summary>
    /// The actual GET. Every failure is raised as <see cref="MarketDataUnavailableException"/> so
    /// the cache never stores a non-page; the caller decides whether that is fatal.
    /// </summary>
    private async Task<string> DownloadDisclosureHtmlAsync(string url, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

        string? html = null;

        try
        {
            using var response = await _httpClient.GetAsync(url, requestTimeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw new MarketDataUnavailableException(
                    $"PSE Edge returned HTTP {(int)response.StatusCode} for {url}. Disclosures are " +
                    "part of the live signal contract, so the run fails.");
            }

            html = await response.Content.ReadAsStringAsync(requestTimeout.Token);
        }
        catch (MarketDataUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            throw new MarketDataUnavailableException(
                $"PSE Edge did not answer within {_options.RequestTimeoutSeconds}s.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MarketDataUnavailableException("Could not reach PSE Edge for disclosures.", ex);
        }

        if (string.IsNullOrWhiteSpace(html))
        {
            throw new MarketDataUnavailableException("PSE Edge returned an empty announcements page.");
        }

        return html;
    }

    /// <summary>
    /// Gives a symbol the history it needs from the exchange itself when the Yahoo chart could
    /// not supply it. Yahoo stopped publishing PSE-listed equities (every <c>{TICKER}.PS</c>
    /// answers 404), so a chart with too few bars is retried against PSE Edge before the ticker is
    /// skipped. The Yahoo reason is kept in the diagnostic either way, which is what makes a
    /// failed run name both feeds instead of only the dead one.
    /// </summary>
    private async Task<ChartFetch> WithEdgeHistoryAsync(
        ChartFetch chart,
        string ticker,
        DateOnly tradeDate,
        int lookbackDays,
        int requiredBars,
        CancellationToken cancellationToken)
    {
        if (_edgeHistory is null || chart.Bars.Count >= requiredBars)
        {
            return chart;
        }

        var edge = await _edgeHistory.FetchDailyBarsAsync(ticker, tradeDate, lookbackDays, cancellationToken);

        if (edge.Bars.Count >= requiredBars)
        {
            _logger.LogInformation(
                "Reading {Ticker} from PSE Edge ({Bars} daily bars) because {Source} ({Reason}).",
                ticker,
                edge.Bars.Count,
                chart.SymbolUnavailable ? "Yahoo Finance has no chart" : "Yahoo Finance has too little history",
                chart.UnavailableReason ?? $"{chart.Bars.Count} bar(s)");

            return chart with
            {
                Bars = edge.Bars,
                SymbolUnavailable = false,
                UnavailableReason = null
            };
        }

        // Edge can answer with real bars that still fall short of the window the caller needs — a
        // newly listed security, or a symbol Edge stopped printing — so the bar count and the
        // newest session belong in the diagnostic rather than leaving it blank.
        var edgeOutcome = edge.Failure ??
            $"{edge.Bars.Count} bar(s), newest {edge.Bars[^1].Date:yyyy-MM-dd}";

        var reason = chart.SymbolUnavailable
            ? $"{chart.UnavailableReason} PSE Edge: {edgeOutcome}."
            : $"{chart.Bars.Count} bar(s). PSE Edge: {edgeOutcome}.";

        return chart.SymbolUnavailable
            ? ChartFetch.Missing(reason)
            : chart with { UnavailableReason = reason };
    }

    /// <summary>
    /// Gives a PSE name the cash dividends the exchange attributes to it. Yahoo's
    /// <c>events.dividends</c> block no longer serves PSE-listed equities, so a chart arrives with
    /// an empty list and every trailing yield is reported as unreported; the exchange's own
    /// dividends table is the only public replacement.
    /// <para>
    /// A chart that already carries dividends is left untouched — that is a feed which answered,
    /// and replacing it with a second source would make the yield depend on which fetch ran. A
    /// failed lookup leaves the list empty, exactly the state it was in before, and is reported
    /// through the feed ledger rather than thrown into the run.
    /// </para>
    /// </summary>
    private async Task<ChartFetch> WithEdgeDividendsAsync(
        ChartFetch chart,
        string ticker,
        DateOnly sessionDate,
        CancellationToken cancellationToken)
    {
        if (_edgeDividends is null || chart.Dividends.Count > 0)
        {
            return chart;
        }

        var dividends = await _edgeDividends.FetchCashDividendsAsync(ticker, sessionDate, cancellationToken);

        return dividends.Count == 0 ? chart : chart with { Dividends = dividends };
    }

    /// <summary>
    /// Fetches every tracked <c>{TICKER}.PS</c> chart with bounded concurrency. Symbols

    /// without a chart, without 200 bars or without a bar on the PSE session date are
    /// skipped with a warning; transport failures are fatal. Fewer than ten surviving
    /// names means the universe is not tradeable, so the run fails.
    /// </summary>
    /// <remarks>
    /// The disclosure map arrives as a task rather than a dictionary so the Edge pages can be
    /// scraped while these charts are downloading: each ticker awaits it only after its own
    /// chart has landed, which is the same point at which it used to read a finished map.
    /// </remarks>
    private async Task<IReadOnlyList<StockMarketData>> FetchStocksAsync(
        DateOnly sessionDate,
        Task<IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>> disclosuresTask,
        int lookbackDays,
        CancellationToken cancellationToken)
    {
        using var throttle = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrency));

        var tasks = PseTickerCatalog.All
            .Select(info => FetchStockAsync(info, sessionDate, disclosuresTask, lookbackDays, throttle, cancellationToken))
            .ToList();

        var results = await Task.WhenAll(tasks);
        var stocks = results.Where(stock => stock is not null).Select(stock => stock!).ToList();

        if (stocks.Count < MinimumStockCount)
        {
            throw new MarketDataUnavailableException(
                $"Only {stocks.Count} tracked PSE tickers had {MinimumStockBars}+ live daily bars on the " +
                $"{sessionDate:yyyy-MM-dd} session; at least {MinimumStockCount} are required to rank a Top 10.");
        }

        return stocks;
    }

    private async Task<StockMarketData?> FetchStockAsync(
        PseTickerInfo info,
        DateOnly sessionDate,
        Task<IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>> disclosuresTask,
        int lookbackDays,
        SemaphoreSlim throttle,
        CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken);

        try
        {
            var chart = await FetchChartAsync(
                info.YahooSymbol,
                sessionDate,
                lookbackDays,
                cancellationToken,
                dropZeroVolume: true);

            chart = await WithEdgeHistoryAsync(
                chart,
                info.Ticker,
                sessionDate,
                lookbackDays,
                MinimumStockBars,
                cancellationToken);

            chart = await WithEdgeDividendsAsync(chart, info.Ticker, sessionDate, cancellationToken);

            if (chart.SymbolUnavailable)
            {
                _logger.LogWarning(
                    "Skipping {Ticker}: no live daily series for {Symbol} ({Reason}).",
                    info.Ticker,
                    info.YahooSymbol,
                    (chart.UnavailableReason ?? "no chart").TrimEnd('.'));
                return null;
            }

            if (chart.Bars.Count < MinimumStockBars)
            {
                _logger.LogWarning(
                    "Skipping {Ticker}: only {Bars} live bars on or before {Date} ({Required} required).",
                    info.Ticker,
                    chart.Bars.Count,
                    sessionDate,
                    MinimumStockBars);
                return null;
            }

            var latest = chart.Bars[^1];
            if (latest.Date != sessionDate)
            {
                _logger.LogWarning(
                    "Skipping {Ticker}: latest live bar is {BarDate} but the PSE session is {SessionDate}.",
                    info.Ticker,
                    latest.Date,
                    sessionDate);
                return null;
            }

            var disclosures = await disclosuresTask;

            var tickerDisclosures = disclosures.TryGetValue(info.Ticker, out var headlines)
                ? headlines
                : Array.Empty<StockDisclosure>();

            return new StockMarketData(
                info.Ticker,
                info.CompanyName,
                info.Sector,
                chart.Bars,
                NetForeignTodayPhp: null,
                tickerDisclosures,
                chart.Dividends);
        }
        finally
        {
            throttle.Release();
        }
    }

    /// <summary>
    /// Calls the Yahoo Finance v8 chart endpoint for one symbol. A non-2xx status,
    /// timeout or transport error is a feed failure (503); a 404 is reported as an
    /// unavailable symbol so callers can skip delisted names.
    /// </summary>
    /// <remarks>
    /// The raw JSON body is cached per chart URL for <see cref="ChartCacheTimeToLive"/>, so the
    /// index, the FX series, every stock and the two-year backtest window reuse one download
    /// instead of repeating it per call site. Parsing stays per call because two callers can ask
    /// for the same body with different tick rounding and volume filters. A failure is never
    /// cached, so the mirror host is tried immediately.
    /// </remarks>
    private async Task<ChartFetch> FetchChartAsync(
        string symbol,
        DateOnly tradeDate,
        int lookbackDays,
        CancellationToken cancellationToken,
        bool roundToTick = true,
        bool dropZeroVolume = false)
    {
        var hosts = YahooHosts();

        for (var hostIndex = 0; hostIndex < hosts.Count; hostIndex++)
        {
            var uri = BuildChartUri(hosts[hostIndex], symbol, tradeDate, lookbackDays);

            try
            {
                // The shared download is not tied to this caller's token: abandoning one request
                // must not cancel a response another request is waiting for. GetChartBodyAsync
                // still applies the configured per-request timeout on its own.
                var body = await _cache.GetOrAddAsync(
                    $"chart:{uri.AbsoluteUri}",
                    ChartCacheTimeToLive,
                    () => GetChartBodyAsync(uri, symbol, CancellationToken.None),
                    cancellationToken);

                if (body is null)
                {
                    return ChartFetch.Missing($"HTTP 404 for {symbol}.");
                }

                var fetch = ParseChart(symbol, body, tradeDate, roundToTick, dropZeroVolume);

                // One observation per answered chart. A body with no series is a skip: the host
                // answered correctly, it simply does not publish this symbol (every {TICKER}.PS
                // is in that state today).
                if (fetch.SymbolUnavailable)
                {
                    _feedHealth.RecordSkipped(
                        FeedSource.YahooCharts,
                        $"{symbol}: {(fetch.UnavailableReason ?? "no series").TrimEnd('.')}");
                }
                else
                {
                    _feedHealth.RecordSuccess(
                        FeedSource.YahooCharts,
                        $"{symbol}: {fetch.Bars.Count} bar(s).",
                        fetch.Bars.Count);
                }

                return fetch;
            }
            catch (MarketDataUnavailableException ex) when (hostIndex < hosts.Count - 1)
            {
                // A transport error or 5xx on the primary host is retried on the mirror host: the
                // two serve the same payload, so a single host outage must not take the desk down.
                // A 404 is host-independent and returns above instead of being retried.
                _logger.LogWarning(
                    ex,
                    "Yahoo host {Host} failed for {Symbol}; retrying on {FallbackHost}.",
                    hosts[hostIndex],
                    symbol,
                    hosts[hostIndex + 1]);
            }
        }

        throw new MarketDataUnavailableException($"Yahoo Finance is unreachable for {symbol}.");
    }

    /// <summary>The hosts tried for a chart request, primary first.</summary>
    private IReadOnlyList<string> YahooHosts()
    {
        var primary = (_options.YahooFinanceBaseUrl ?? string.Empty).Trim();
        var fallback = (_options.YahooFinanceFallbackBaseUrl ?? string.Empty).Trim();
        var hosts = new List<string>(2);

        if (primary.Length > 0)
        {
            hosts.Add(primary);
        }

        if (fallback.Length > 0 && !string.Equals(fallback, primary, StringComparison.OrdinalIgnoreCase))
        {
            hosts.Add(fallback);
        }

        return hosts.Count > 0 ? hosts : new List<string> { "https://query1.finance.yahoo.com" };
    }

    /// <summary>
    /// One chart GET. Returns null for a 404 (the symbol is not published), which callers turn
    /// into a skipped ticker, and raises <see cref="MarketDataUnavailableException"/> for every
    /// other failure so the caller can decide whether to retry the mirror host.
    /// </summary>
    private async Task<string?> GetChartBodyAsync(Uri uri, string symbol, CancellationToken cancellationToken)
    {
        using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

        try
        {
            using var response = await _httpClient.GetAsync(uri, requestTimeout.Token);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                _feedHealth.RecordSkipped(
                    FeedSource.YahooCharts,
                    $"{symbol}: HTTP 404 (Yahoo does not publish this symbol).");
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _feedHealth.RecordFailure(
                    FeedSource.YahooCharts,
                    $"{symbol}: HTTP {(int)response.StatusCode}.");

                throw new MarketDataUnavailableException(
                    $"Yahoo Finance returned HTTP {(int)response.StatusCode} for {symbol}. " +
                    "The live feed is unavailable, so no signal can be produced.");
            }

            return await response.Content.ReadAsStringAsync(requestTimeout.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A caller abort (tab closed, request replaced) is never a feed failure.
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _feedHealth.RecordFailure(
                FeedSource.YahooCharts,
                $"{symbol}: no answer within {_options.RequestTimeoutSeconds}s.");

            throw new MarketDataUnavailableException(
                $"Yahoo Finance did not answer for {symbol} within {_options.RequestTimeoutSeconds}s.",
                ex);
        }
        catch (HttpRequestException ex)
        {
            _feedHealth.RecordFailure(
                FeedSource.YahooCharts,
                $"{symbol}: transport error ({ex.GetType().Name}).");

            throw new MarketDataUnavailableException($"Could not reach Yahoo Finance for {symbol}.", ex);
        }
    }

    private Uri BuildChartUri(string baseUrl, string symbol, DateOnly tradeDate, int lookbackDays)
    {
        var origin = baseUrl.TrimEnd('/');
        var lookback = Math.Max(1, lookbackDays);
        var period1 = StartOfManilaDay(tradeDate.AddDays(-lookback));
        var period2 = EndOfManilaDay(tradeDate);

        return new Uri(
            $"{origin}/v8/finance/chart/{Uri.EscapeDataString(symbol)}" +
            $"?interval=1d&includePrePost=false&events=div&period1={period1}&period2={period2}");
    }

    private static long StartOfManilaDay(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, ManilaTimeZone.Instance.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    private static long EndOfManilaDay(DateOnly date)
    {
        var local = date.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, ManilaTimeZone.Instance.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    /// <summary>
    /// Parses <c>chart.result[0].timestamp</c> plus
    /// <c>chart.result[0].indicators.quote[0]</c> into daily bars, keeping only sessions on or
    /// before the requested trade date. PSE candles are rounded onto the exchange tick grid;
    /// macro series (yields, Brent, EPHE) pass <paramref name="roundToTick"/> as false because a
    /// 4.352% yield or a USD 71.83 barrel is not orderable on the PSE.
    /// </summary>
    private static ChartFetch ParseChart(
        string symbol,
        string body,
        DateOnly tradeDate,
        bool roundToTick = true,
        bool dropZeroVolume = false)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new MarketDataUnavailableException($"Yahoo Finance returned malformed JSON for {symbol}.", ex);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("chart", out var chart) ||
                chart.ValueKind != JsonValueKind.Object)
            {
                throw new MarketDataUnavailableException(
                    $"Yahoo Finance returned a payload without a chart section for {symbol}.");
            }

            if (chart.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                return ChartFetch.Missing($"Yahoo Finance error for {symbol}: {DescribeError(error)}");
            }

            if (!chart.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Array ||
                result.GetArrayLength() == 0)
            {
                return ChartFetch.Missing($"Yahoo Finance returned no chart result for {symbol}.");
            }

            var node = result[0];
            var dividends = ReadDividends(node, tradeDate);

            // The quote block carries the live index reading and whether the regular session that
            // produced it has already closed. Both are read before the bars so a partial candle can
            // be recognised instead of being stored as a finished session.
            var meta = node.TryGetProperty("meta", out var metaElement) && metaElement.ValueKind == JsonValueKind.Object
                ? metaElement
                : (JsonElement?)null;
            var livePrice = ReadMetaPrice(meta);
            var (liveDate, sessionInProgress) = ReadSessionProgress(meta);

            if (!node.TryGetProperty("timestamp", out var timestamps) ||
                timestamps.ValueKind != JsonValueKind.Array)
            {
                return ChartFetch.Empty($"Yahoo Finance returned no timestamps for {symbol}.");
            }

            if (!node.TryGetProperty("indicators", out var indicators) ||
                !indicators.TryGetProperty("quote", out var quotes) ||
                quotes.ValueKind != JsonValueKind.Array ||
                quotes.GetArrayLength() == 0)
            {
                return ChartFetch.Empty($"Yahoo Finance returned no quote block for {symbol}.");
            }

            var quote = quotes[0];
            var opens = ReadNumericArray(quote, "open");
            var highs = ReadNumericArray(quote, "high");
            var lows = ReadNumericArray(quote, "low");
            var closes = ReadNumericArray(quote, "close");
            var volumes = ReadNumericArray(quote, "volume");

            var count = Math.Min(
                timestamps.GetArrayLength(),
                Math.Min(opens.Length, Math.Min(highs.Length, Math.Min(lows.Length, closes.Length))));

            var bars = new List<OhlcvBar>(count);

            for (var i = 0; i < count; i++)
            {
                if (!TryReadSessionDate(timestamps[i], out var date) || date > tradeDate)
                {
                    continue;
                }

                if (opens[i] is not decimal open ||
                    highs[i] is not decimal high ||
                    lows[i] is not decimal low ||
                    closes[i] is not decimal close ||
                    close <= 0m)
                {
                    continue;
                }

                var volume = volumes.Length > i && volumes[i] is decimal rawVolume && rawVolume > 0m
                    ? (long)decimal.Round(rawVolume, 0, MidpointRounding.AwayFromZero)
                    : 0L;

                bars.Add(roundToTick
                    ? MakeBar(date, open, high, low, close, volume)
                    : new OhlcvBar(date, open, high, low, close, volume));
            }

            // A session that is still trading has an unfinished candle: its high/low/close are a
            // snapshot, not a settled bar. Storing it would let an intraday print decide the 50D
            // trend, so it is dropped and the run uses the last completed session.
            if (sessionInProgress &&
                liveDate is DateOnly runningDate &&
                bars.Count > 0 &&
                bars[^1].Date == runningDate)
            {
                bars.RemoveAt(bars.Count - 1);
            }

            if (dropZeroVolume)
            {
                bars = DropZeroVolumeEchoes(bars);
            }

            return bars.Count == 0
                ? ChartFetch.Empty(
                    $"Yahoo Finance returned no usable daily bars for {symbol} through {tradeDate:yyyy-MM-dd}.")
                : new ChartFetch(bars, SymbolUnavailable: false, UnavailableReason: null, dividends, livePrice, liveDate);
        }
    }

    /// <summary>
    /// Drops placeholder candles: a bar the feed prints with no volume that only repeats the
    /// previous close is a fill-in row for a session with no trades, not an observed price. Such
    /// rows would otherwise enter the moving averages as flat, traded sessions.
    /// </summary>
    private static List<OhlcvBar> DropZeroVolumeEchoes(List<OhlcvBar> bars)
    {
        var kept = new List<OhlcvBar>(bars.Count);

        foreach (var bar in bars)
        {
            var echoesPrevious = kept.Count > 0 &&
                                 bar.Volume == 0L &&
                                 bar.Open == kept[^1].Close &&
                                 bar.High == kept[^1].Close &&
                                 bar.Low == kept[^1].Close &&
                                 bar.Close == kept[^1].Close;

            if (!echoesPrevious)
            {
                kept.Add(bar);
            }
        }

        return kept;
    }

    private static decimal? ReadMetaPrice(JsonElement? meta)
    {
        if (meta is not JsonElement element ||
            !element.TryGetProperty("regularMarketPrice", out var price) ||
            price.ValueKind != JsonValueKind.Number ||
            !price.TryGetDecimal(out var value) ||
            value <= 0m)
        {
            return null;
        }

        return value;
    }

    /// <summary>
    /// The Manila date of the live quote and whether its regular session is still running. A
    /// payload without <c>meta</c> (a test fixture, a trimmed response) reports neither, and the
    /// caller keeps every finished bar.
    /// </summary>
    private static (DateOnly? LiveDate, bool SessionInProgress) ReadSessionProgress(JsonElement? meta)
    {
        if (meta is not JsonElement element ||
            !element.TryGetProperty("regularMarketTime", out var timeElement) ||
            timeElement.ValueKind != JsonValueKind.Number ||
            !timeElement.TryGetInt64(out var marketTime) ||
            !TryReadManilaDate(marketTime, out var liveDate))
        {
            return (null, false);
        }

        var inProgress = false;

        if (element.TryGetProperty("currentTradingPeriod", out var period) &&
            period.ValueKind == JsonValueKind.Object &&
            period.TryGetProperty("regular", out var regular) &&
            regular.ValueKind == JsonValueKind.Object &&
            regular.TryGetProperty("end", out var endElement) &&
            endElement.ValueKind == JsonValueKind.Number &&
            endElement.TryGetInt64(out var endSeconds) &&
            endSeconds > 0)
        {
            inProgress = marketTime < endSeconds;
        }

        return (liveDate, inProgress);
    }

    private static decimal?[] ReadNumericArray(JsonElement container, string propertyName)
    {
        if (!container.TryGetProperty(propertyName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<decimal?>();
        }

        var values = new decimal?[array.GetArrayLength()];

        for (var i = 0; i < values.Length; i++)
        {
            var element = array[i];
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out var value))
            {
                values[i] = value;
            }
        }

        return values;
    }

    /// <summary>
    /// Reads <c>chart.result[0].events.dividends</c> (Yahoo's <c>events=div</c> payload) into
    /// ticker-history dividend events. Announced future ex-dates are dropped: a dividend that
    /// has not gone ex yet is not part of a trailing-twelve-month yield.
    /// </summary>
    private static IReadOnlyList<StockDividendEvent> ReadDividends(JsonElement node, DateOnly tradeDate)
    {
        if (!node.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Object ||
            !events.TryGetProperty("dividends", out var dividends) ||
            dividends.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<StockDividendEvent>();
        }

        var result = new List<StockDividendEvent>();

        foreach (var property in dividends.EnumerateObject())
        {
            var entry = property.Value;
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!entry.TryGetProperty("amount", out var amountElement) ||
                amountElement.ValueKind != JsonValueKind.Number ||
                !amountElement.TryGetDecimal(out var amount) ||
                amount <= 0m)
            {
                continue;
            }

            if (!entry.TryGetProperty("date", out var dateElement) ||
                dateElement.ValueKind != JsonValueKind.Number ||
                !dateElement.TryGetInt64(out var seconds) ||
                !TryReadManilaDate(seconds, out var exDate))
            {
                continue;
            }

            if (exDate > tradeDate)
            {
                continue;
            }

            result.Add(new StockDividendEvent(
                exDate,
                decimal.Round(amount, 4, MidpointRounding.AwayFromZero)));
        }

        return result
            .GroupBy(dividend => dividend.ExDate)
            .Select(group => group.OrderByDescending(dividend => dividend.AmountPerShare).First())
            .OrderBy(dividend => dividend.ExDate)
            .ToList();
    }

    private static bool TryReadSessionDate(JsonElement element, out DateOnly date)
    {
        date = default;

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var seconds))
        {
            return false;
        }

        return TryReadManilaDate(seconds, out date);
    }

    private static bool TryReadManilaDate(long seconds, out DateOnly date)
    {
        var manila = TimeZoneInfo.ConvertTime(
            DateTimeOffset.FromUnixTimeSeconds(seconds),
            ManilaTimeZone.Instance);

        date = DateOnly.FromDateTime(manila.DateTime);
        return true;
    }

    /// <summary>
    /// Rounds OHLC onto the PSE tick grid and re-establishes high/low containment so a
    /// persisted candle can never print an unorderable price.
    /// </summary>
    private static OhlcvBar MakeBar(DateOnly date, decimal open, decimal high, decimal low, decimal close, long volume)
    {
        var roundedClose = PseBoardLotService.RoundToValidTick(close);
        var roundedOpen = PseBoardLotService.RoundToValidTick(open);

        if (roundedOpen <= 0m)
        {
            roundedOpen = roundedClose;
        }

        var roundedHigh = PseBoardLotService.RoundToValidTick(Math.Max(high, Math.Max(open, close)));
        var roundedLow = PseBoardLotService.RoundToValidTick(Math.Min(low, Math.Min(open, close)));

        roundedHigh = Math.Max(roundedHigh, Math.Max(roundedOpen, roundedClose));
        roundedLow = Math.Min(roundedLow, Math.Min(roundedOpen, roundedClose));

        if (roundedLow <= 0m)
        {
            roundedLow = Math.Min(roundedOpen, roundedClose);
        }

        return new OhlcvBar(date, roundedOpen, roundedHigh, roundedLow, roundedClose, volume);
    }

    private static string DescribeError(JsonElement error)
    {
        var code = error.TryGetProperty("code", out var codeElement) ? codeElement.GetString() : null;
        var description = error.TryGetProperty("description", out var descriptionElement)
            ? descriptionElement.GetString()
            : null;

        return string.Join(": ", new[] { code, description }.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static string BuildRegimeNote(
        decimal close,
        decimal sma50,
        decimal sma200,
        UsdPhpQuote usdPhp,
        decimal? pseiRealizedVol20dPct,
        decimal? netForeignFlowPhp,
        string? foreignFlowSource,
        IndexSeries index,
        MacroBenchmarks macro)
    {
        var note = new StringBuilder(620);

        if (index.IsProxy)
        {
            // The index quote exists but its history does not, so the averages are honestly
            // absent: printing a rebased proxy level as a PSEi moving average would be a lie.
            note.Append(close > 0m
                ? $"PSEi last traded at {FormatNumber(close, "0.##")}"
                : "The PSEi level is unreported for this session");

            note.Append($". The index carries no published history, so the trend is read from an " +
                        $"equal-weight proxy of {index.ProxyConstituentCount} PSE heavyweights, and the index " +
                        "50D/200D averages are unreported");
        }
        else
        {
            note.Append($"PSEi closed at {FormatNumber(close, "0.##")} versus its 50D average of ")
                .Append($"{FormatNumber(sma50, "0.##")} and 200D average of {FormatNumber(sma200, "0.##")}");
        }

        if (pseiRealizedVol20dPct is decimal realizedVol)
        {
            note.Append($"; 20-session realised volatility is {FormatNumber(realizedVol, "0.##")}% annualised");
        }

        note.Append($". USD/PHP is {FormatNumber(usdPhp.Last, "0.####")}");

        if (usdPhp.Sma50 is decimal fxSma50)
        {
            note.Append(usdPhp.Last >= fxSma50
                ? $", above its 50D average of {FormatNumber(fxSma50, "0.####")} (peso weak)"
                : $", below its 50D average of {FormatNumber(fxSma50, "0.####")} (peso firm)");
        }

        if (usdPhp.Change20dPct is decimal fxChange)
        {
            note.Append($", {FormatNumber(fxChange, "0.##")}% over 20 sessions");
        }

        if (netForeignFlowPhp is decimal netForeign)
        {
            var provenance = foreignFlowSource switch
            {
                "pse-dqr-upload" => "official PSE Daily Quotation Report, uploaded",
                "pse-dqr-live" => "official PSE Daily Quotation Report, live",
                _ => "official PSE Daily Quotation Report"
            };

            note.Append($". Net foreign flow is PHP {FormatNumber(netForeign, "0.##")} ({provenance})");
        }
        else
        {
            note.Append(". Net foreign flow is unreported: no official Daily Quotation Report was available " +
                        "for this session, and neither Yahoo Finance nor the PSE Edge page publishes it");
        }

        if (macro.Us10YClosePct is decimal us10Y)
        {
            note.Append($". US 10-year yield is {FormatNumber(us10Y, "0.###")}%");

            if (macro.Us10YChange20dBps is decimal yieldChange)
            {
                note.Append($" ({FormatNumber(yieldChange, "0.#")} bps over 20 sessions)");
            }
        }

        if (macro.BrentUsd is decimal brent)
        {
            note.Append($". Brent crude is USD {FormatNumber(brent, "0.##")}/bbl");

            if (macro.BrentChange20dPct is decimal brentChange)
            {
                note.Append($" ({FormatNumber(brentChange, "0.##")}% over 20 sessions)");
            }
        }

        if (macro.EpheCloseUsd is decimal ephe)
        {
            note.Append($". EPHE (offshore PH beta) is USD {FormatNumber(ephe, "0.##")}");

            if (macro.EpheChange20dPct is decimal epheChange)
            {
                note.Append($" ({FormatNumber(epheChange, "0.##")}% over 20 sessions)");
            }
        }

        note.Append('.');
        return note.ToString();
    }

    /// <summary>
    /// Extracts filings from an Edge result page. Each table row is read cell by cell, so the
    /// company cell and its subject cell stay together: the old anchor-per-row reading produced a
    /// bare company name and a bare subject as two unrelated filings, which is why a disclosure
    /// could be attributed to the wrong issuer. Rows without a link, the date cell, page furniture
    /// and short fragments are dropped. When the response is not a table (or a test fixture), the
    /// anchor/title text is used with the session date as the filing date.
    /// </summary>
    private static IReadOnlyList<RawFiling> ExtractFilings(string html, DateOnly fallbackDate)
    {
        var filings = new List<RawFiling>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match rowMatch in TableRowRegex.Matches(html))
        {
            var row = rowMatch.Groups["row"].Value;

            // A real result row links to the filing; header and spacer rows do not.
            if (!row.Contains("<a", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var texts = ExtractRowTexts(row);

            if (texts.Count == 0)
            {
                continue;
            }

            var date = ParseFilingDate(row) ?? fallbackDate;

            if (texts.Count == 1)
            {
                if (seen.Add(texts[0]))
                {
                    filings.Add(new RawFiling(date, texts[0]));
                }

                continue;
            }

            // One filing per subject: the company leads, every further cell is a subject the row
            // belongs to.
            foreach (var subject in texts.Skip(1))
            {
                var headline = $"{texts[0]} — {subject}";

                if (seen.Add(headline))
                {
                    filings.Add(new RawFiling(date, headline));
                }
            }
        }

        if (filings.Count > 0)
        {
            return filings;
        }

        return ExtractHeadlines(html)
            .Select(headline => new RawFiling(fallbackDate, headline))
            .ToList();
    }

    /// <summary>
    /// The meaningful text of each cell in one table row, in document order: the anchor text when
    /// the cell is a link (Edge links the company and the subject), otherwise the cell's own text.
    /// </summary>
    private static List<string> ExtractRowTexts(string row)
    {
        var texts = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match cellMatch in TableCellRegex.Matches(row))
        {
            var cell = cellMatch.Groups["cell"].Value;
            var anchors = AnchorTextRegex.Matches(cell)
                .Select(match => CleanText(match.Groups["text"].Value))
                .Where(text => text.Length > 0)
                .ToList();

            var candidates = anchors.Count > 0
                ? anchors
                : new List<string> { CleanText(cell) };

            foreach (var candidate in candidates)
            {
                if (candidate.Length < 3 || NoiseTextRegex.IsMatch(candidate))
                {
                    continue;
                }

                // The filing timestamp is already parsed from the row.
                if (candidate.Length <= 24 && ParseFilingDate(candidate) is not null)
                {
                    continue;
                }

                // The PSE form and report numbers that follow the subject on the live disclosure
                // table are identifiers, not subjects: read as headlines they turned one filing
                // into three and pushed "— C08413-2026" into the prompt.
                if (candidate.Length <= 20 && FilingIdentifierRegex.IsMatch(candidate))
                {
                    continue;
                }

                if (seen.Add(candidate))
                {
                    texts.Add(candidate);
                }
            }
        }

        return texts;
    }

    private static DateOnly? ParseFilingDate(string text)
    {
        var numeric = FilingDateRegex.Match(text);

        if (numeric.Success)
        {
            return BuildDate(
                int.Parse(numeric.Groups["m"].Value, CultureInfo.InvariantCulture),
                int.Parse(numeric.Groups["d"].Value, CultureInfo.InvariantCulture),
                int.Parse(numeric.Groups["y"].Value, CultureInfo.InvariantCulture));
        }

        var spelled = AnnounceDateRegex.Match(text);

        if (!spelled.Success)
        {
            return null;
        }

        var month = Array.FindIndex(
            MonthAbbreviations,
            name => name.Equals(spelled.Groups["mon"].Value, StringComparison.OrdinalIgnoreCase)) + 1;

        return month == 0
            ? null
            : BuildDate(
                month,
                int.Parse(spelled.Groups["d"].Value, CultureInfo.InvariantCulture),
                int.Parse(spelled.Groups["y"].Value, CultureInfo.InvariantCulture));
    }

    private static DateOnly? BuildDate(int month, int day, int year) =>
        month is < 1 or > 12 ||
        day < 1 ||
        day > DateTime.DaysInMonth(year, month)
            ? null
            : new DateOnly(year, month, day);

    private static string FormatNumber(decimal value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static List<string> ExtractHeadlines(string html)
    {
        var headlines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in AnchorTextRegex.Matches(html))
        {
            AddHeadline(headlines, match.Groups["text"].Value);
        }

        foreach (Match match in TitleAttributeRegex.Matches(html))
        {
            AddHeadline(headlines, match.Groups["text"].Value);
        }

        return headlines.ToList();
    }

    private static void AddHeadline(ISet<string> headlines, string raw)
    {
        var text = CleanText(raw);

        if (text.Length >= 15)
        {
            headlines.Add(text);
        }
    }

    /// <summary>Strips markup, decodes entities and collapses whitespace.</summary>
    private static string CleanText(string raw)
    {
        var text = Regex.Replace(raw ?? string.Empty, "<.*?>", " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, "\\s+", " ").Trim();

        return text;
    }

    /// <summary>
    /// How strongly a headline belongs to one catalog name. <see cref="CompanyMatch.Tier"/> is 2
    /// for a bare ticker token and 3 for an explicit company phrase or alias, and
    /// <see cref="CompanyMatch.Length"/> ranks equally tiered matches by how much of the name they
    /// cover, so "San Miguel Food and Beverage" beats the shorter "San Miguel" of its parent.
    /// Tickers shorter than three characters are never token-matched: as substrings they live
    /// inside ordinary words, and as tokens they are prefixes of other listings.
    /// </summary>
    private static CompanyMatch MatchStrength(string headline, PseTickerInfo info)
    {
        if (string.IsNullOrWhiteSpace(headline))
        {
            return default;
        }

        var normalizedHeadline = NormalizeForMatch(headline);

        foreach (var phrase in EnumeratePhrases(info))
        {
            if (phrase.Length >= 4 && ContainsPhrase(normalizedHeadline, phrase))
            {
                return new CompanyMatch(3, phrase.Length);
            }
        }

        return info.Ticker.Length >= 3 && ContainsToken(headline, info.Ticker)
            ? new CompanyMatch(2, info.Ticker.Length)
            : default;
    }

    private static IEnumerable<string> EnumeratePhrases(PseTickerInfo info)
    {
        yield return NormalizeForMatch(info.CompanyName);
        yield return NormalizeForMatch(StripTrailingLegalWords(info.CompanyName));

        foreach (var alias in info.SearchAliases ?? Array.Empty<string>())
        {
            yield return NormalizeForMatch(alias);
        }

        foreach (var pattern in info.DisclosurePatterns ?? Array.Empty<string>())
        {
            yield return NormalizeForMatch(pattern);
        }
    }

    /// <summary>
    /// The company name without its trailing entity words, which a headline usually abbreviates
    /// ("…, Inc." / "… Corp."). At least two words are kept, so the result stays a name.
    /// </summary>
    private static string StripTrailingLegalWords(string companyName)
    {
        var words = (companyName ?? string.Empty)
            .Split(new[] { ' ', ',', '.', '(', ')', '&' }, StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        while (words.Count > 2 && LegalSuffixWords.Contains(words[^1]))
        {
            words.RemoveAt(words.Count - 1);
        }

        return string.Join(' ', words);
    }

    private static string NormalizeForMatch(string value)
    {
        var builder = new StringBuilder(value?.Length ?? 0);
        var previousWasSpace = true;

        foreach (var character in (value ?? string.Empty).ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousWasSpace = false;
            }
            else if (!previousWasSpace)
            {
                builder.Append(' ');
                previousWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Whole-phrase containment on the normalized text, so "sm prime" never matches "sm primex"
    /// and a dash or bracket between words does not break the match.
    /// </summary>
    private static bool ContainsPhrase(string normalizedHeadline, string phrase) =>
        ($" {normalizedHeadline} ").Contains($" {phrase} ", StringComparison.Ordinal);

    /// <summary>
    /// Whether the ticker appears as a standalone alphanumeric token. As a substring it would also
    /// match inside ordinary words ("DITO" in "auditor", "PLUS" in "surplus").
    /// </summary>
    private static bool ContainsToken(string headline, string ticker)
    {
        var index = 0;

        while (index < headline.Length)
        {
            var found = headline.IndexOf(ticker, index, StringComparison.OrdinalIgnoreCase);

            if (found < 0)
            {
                return false;
            }

            var startsAtBoundary = found == 0 || !char.IsLetterOrDigit(headline[found - 1]);
            var afterIndex = found + ticker.Length;
            var endsAtBoundary = afterIndex >= headline.Length || !char.IsLetterOrDigit(headline[afterIndex]);

            if (startsAtBoundary && endsAtBoundary)
            {
                return true;
            }

            index = afterIndex;
        }

        return false;
    }

    private readonly record struct CompanyMatch(int Tier, int Length);

    /// <summary>
    /// The market-trend series resolved for a session: either the index's own bars, or an
    /// equal-weight proxy of heavyweight returns when the index carries no history. In the proxy
    /// case <see cref="LiveClose"/> is the exchange's own index quote (used only as the reported
    /// level) and <see cref="ProxyConstituentCount"/> records how many names backed it.
    /// </summary>
    private sealed record IndexSeries(
        IReadOnlyList<OhlcvBar> Bars,
        bool IsProxy,
        decimal? LiveClose,
        int ProxyConstituentCount);

    /// <summary>
    /// Snapshot of one chart request: either usable bars, or the reason the symbol has
    /// no usable series (so equities can be skipped while macro failures stay fatal).
    /// <para>
    /// <see cref="LivePrice"/> and <see cref="LiveDate"/> come from the payload's <c>meta</c>
    /// block and describe the latest quote, which can exist even when the series carries no
    /// history at all (Yahoo currently answers <c>PSEI.PS</c> that way).
    /// </para>
    /// </summary>
    private sealed record ChartFetch(
        IReadOnlyList<OhlcvBar> Bars,
        bool SymbolUnavailable,
        string? UnavailableReason,
        IReadOnlyList<StockDividendEvent> Dividends,
        decimal? LivePrice = null,
        DateOnly? LiveDate = null)
    {
        public static ChartFetch Missing(string reason) =>
            new(Array.Empty<OhlcvBar>(), true, reason, Array.Empty<StockDividendEvent>());

        public static ChartFetch Empty(string reason) =>
            new(Array.Empty<OhlcvBar>(), false, reason, Array.Empty<StockDividendEvent>());
    }

    /// <summary>One scraped Edge filing with the date the exchange published it.</summary>
    private sealed record RawFiling(DateOnly Date, string Headline);

    /// <summary>
    /// One heavyweight proxy constituent's outcome: the real bars when the feed published them,
    /// or the reason the name was skipped, so the failure message can explain every constituent.
    /// </summary>
    private sealed record ProxyConstituentFetch(IReadOnlyList<OhlcvBar>? Bars, string? Reason);
}
