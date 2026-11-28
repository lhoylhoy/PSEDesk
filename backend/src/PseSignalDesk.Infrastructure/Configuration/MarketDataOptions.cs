namespace PseSignalDesk.Infrastructure.Configuration;

/// <summary>
/// Live market-data configuration. Every value here points at a real upstream feed:
/// there are no demo or synthetic fallbacks behind these settings.
/// </summary>
public sealed class MarketDataOptions
{
    public const string SectionName = "MarketData";

    /// <summary>
    /// Yahoo Finance origin used for the v8 chart endpoint
    /// (<c>/v8/finance/chart/{symbol}</c>).
    /// </summary>
    public string YahooFinanceBaseUrl { get; set; } = "https://query1.finance.yahoo.com";

    /// <summary>
    /// Second Yahoo origin, tried when the primary host fails with a transport or 5xx error.
    /// The two hosts serve the same payload; falling through only avoids a single-host outage
    /// taking the desk down.
    /// </summary>
    public string YahooFinanceFallbackBaseUrl { get; set; } = "https://query2.finance.yahoo.com";

    /// <summary>
    /// Index symbols tried, in order, for the PSEi series. Yahoo currently answers
    /// <c>PSEI.PS</c> with a single live bar and no history, so the provider cannot rely on one
    /// symbol: it walks this list and, if none carries enough history, falls back to the
    /// equal-weight constituent proxy below.
    /// </summary>
    public IList<string> IndexYahooSymbols { get; set; } = new List<string> { "PSEI.PS" };

    /// <summary>
    /// Heavyweight tickers used to derive the market trend when no index series carries history.
    /// The proxy is equal-weighted and built only from real constituent closes, so it can drive
    /// the regime (a scale-free comparison) but never substitutes an index <em>level</em>: the
    /// level still comes from the index quote, and the index moving averages stay unreported.
    /// </summary>
    public IList<string> IndexProxyConstituents { get; set; } = new List<string>
    {
        "TEL", "SM", "SMPH", "ALI", "BDO", "BPI", "ICT", "JFC", "MER", "AC", "URC", "GLO"
    };

    /// <summary>
    /// PSE Edge announcement search page scraped for disclosure headlines.
    /// </summary>
    public string PseEdgeAnnouncementsUrl { get; set; } = "https://edge.pse.com.ph/announcements/search.ax";

    /// <summary>
    /// Whether a symbol whose Yahoo Finance chart carries no usable series is re-read from PSE
    /// Edge's own daily history. Yahoo no longer publishes PSE-listed equities — every
    /// <c>{TICKER}.PS</c> symbol answers 404 while <c>PSEI.PS</c> keeps only a live level and a
    /// legacy quarterly series — so this is what keeps a run live-only instead of failing. Set it
    /// to <c>false</c> to restore the strict Yahoo-only contract.
    /// </summary>
    public bool EnablePseEdgeHistory { get; set; } = true;

    /// <summary>
    /// PSE Edge daily chart endpoint. A JSON <c>POST</c> of
    /// <c>{ cmpy_id, security_id, startDate, endDate }</c> answers
    /// <c>{ chartData: [{ OPEN, HIGH, LOW, CLOSE, VALUE, CHART_DATE }] }</c>: the exchange's own
    /// daily bars, with the peso value traded but no share count.
    /// </summary>
    public string PseEdgeChartUrl { get; set; } = "https://edge.pse.com.ph/common/DisclosureCht.ax";

    /// <summary>PSE Edge symbol search, used to resolve a ticker to its Edge <c>cmpy_id</c>.</summary>
    public string PseEdgeSymbolSearchUrl { get; set; } =
        "https://edge.pse.com.ph/autoComplete/searchCompanyNameSymbol.ax";

    /// <summary>
    /// PSE Edge company page, used to resolve the common security's <c>security_id</c> for a
    /// company (a company can list preferred series next to the common).
    /// </summary>
    public string PseEdgeCompanyPageUrl { get; set; } = "https://edge.pse.com.ph/companyPage/stockData.do";

    /// <summary>
    /// PSE Edge company-disclosure search page. It is polled market-wide (no query) alongside the
    /// announcement page and is best-effort: a failure there never blocks a run, because the
    /// announcement page is the contract.
    /// <para>
    /// The same endpoint serves one company's own filing table when its <c>keyword</c> is the
    /// exchange's numeric <c>cmpy_id</c> — the value the page's own search form posts —
    /// which is what <see cref="DisclosureCompanyFanoutLimit"/> exercises.
    /// </para>
    /// </summary>
    public string PseEdgeCompanyDisclosuresUrl { get; set; } =
        "https://edge.pse.com.ph/companyDisclosures/search.ax";

    /// <summary>
    /// PSE Edge per-company dividends table. The page shell
    /// (<c>companyPage/dividends_and_rights_form.do?cmpy_id=</c>) loads this list endpoint by
    /// <c>POST</c> with the company's numeric <c>cmpy_id</c>, answering an HTML row table
    /// (<c>Type of Security | Type of Dividend | Dividend Rate | Ex-Dividend Date | Record Date |
    /// Payment Date | Circular Number</c>). It is the only public source of PSE cash dividends:
    /// Yahoo's chart <c>events.dividends</c> no longer serves PSE names, which is why every
    /// trailing yield was reported as unreported.
    /// </summary>
    public string PseEdgeDividendsUrl { get; set; } =
        "https://edge.pse.com.ph/companyPage/dividends_and_rights_list.ax";

    /// <summary>
    /// Whether a ticker whose chart carries no dividend events is re-read from the exchange's own
    /// dividends table. On by default: without it the trailing yield, the cash-yield sleeve and
    /// the dividend-versus-US10Y dial are all inert. Set it to <c>false</c> to make no dividend
    /// request at all.
    /// <para>
    /// The read also rides with <see cref="EnablePseEdgeHistory"/>: both are the same exchange
    /// host, and turning the Edge history off restores a Yahoo-only run with no Edge traffic at
    /// all. This switch is the narrower rollback — keep the Edge bars, stop the dividend fan-out.
    /// </para>
    /// </summary>
    public bool EnablePseEdgeDividends { get; set; } = true;

    /// <summary>
    /// How long the dividends table stays cached, in minutes. Dividends are declared a few times
    /// a year, so this is measured in hours (default six) rather than the ten minutes a daily bar
    /// gets: four horizon runs in one session would otherwise download the same unchanged table
    /// four times. Values below one fall back to the source's own default.
    /// </summary>
    public int PseEdgeDividendsCacheMinutes { get; set; } = 360;

    /// <summary>
    /// How many of the highest-scoring names that clear the liquidity floor are re-read through
    /// their own PSE Edge company page, so a filing is attributed by the exchange's <c>cmpy_id</c>
    /// instead of by matching its headline to a company name.
    /// <para>
    /// The market-wide page carries no company column, so a template like "Mergers and
    /// Consolidations" cannot be attributed at all and the structural trap screen goes unenforced
    /// without any error. The per-company page answers exactly that gap: one GET per name, capped
    /// here at 25 (the desk's own candidate cap) so a run costs at most 25 extra requests. Set to
    /// <c>0</c> to switch the fan-out off and fall back to the market-wide scrape alone.
    /// </para>
    /// </summary>
    public int DisclosureCompanyFanoutLimit { get; set; } = 25;

    /// <summary>
    /// Number of Edge result pages scanned per listing. One page covers roughly a session; two
    /// pages cover a long weekend or a busy filing day, so a Friday rights offering is still
    /// visible on Monday. Extra pages are best-effort.
    /// </summary>
    public int DisclosurePagesToScan { get; set; } = 2;

    /// <summary>
    /// Date-addressed templates for the official PSE Daily Quotation Report, the only public
    /// source of net foreign flow. Supported tokens: <c>{yyyy}</c>, <c>{MMddyyyy}</c> and
    /// <c>{MMddyy}</c>. Empty by default: the exchange does not publish a stable date-addressed
    /// URL, so operators either configure an <b>authorised or licensed</b> endpoint (or a local
    /// file server they control) here, or upload the report through
    /// <c>POST /api/signals/quotation-report</c>. Pointing these templates at a third-party
    /// mirror of the exchange's report is not permitted: it would redistribute PSE market data
    /// without a redistribution agreement. Any failure is best-effort and leaves foreign flow
    /// unreported (null), never zero.
    /// </summary>
    public IList<string> PseDailyQuotationUrlTemplates { get; set; } = new List<string>();

    /// <summary>
    /// User-Agent sent with every upstream market-data request. It names the application
    /// honestly instead of impersonating a browser: spoofing a Chrome UA to get around an
    /// upstream anti-scraping control is both a breach of the feed's terms of use and a
    /// misrepresentation this project does not make. Operators with a licensed feed may set
    /// their own identifier here.
    /// </summary>
    public string UserAgent { get; set; } = "PseSignalDesk-PersonalResearch/0.1 (local-noncommercial)";

    /// <summary>
    /// Yahoo Finance symbols for the cross-asset macro dials. <c>^TNX</c> is the US 10-year
    /// Treasury yield in percent, <c>BZ=F</c> is Brent crude in USD and <c>EPHE</c> is the
    /// iShares MSCI Philippines ETF.
    /// </summary>
    public string Us10YahooSymbol { get; set; } = "^TNX";

    public string BrentYahooSymbol { get; set; } = "BZ=F";

    public string EpheYahooSymbol { get; set; } = "EPHE";

    /// <summary>
    /// Calendar lookback for each chart request. 420 calendar days is roughly 280 PSE
    /// trading days, comfortably above the 200-bar trend requirement.
    /// </summary>
    public int LookbackCalendarDays { get; set; } = 420;

    /// <summary>
    /// Maximum concurrent Yahoo Finance requests for the ticker universe.
    /// </summary>
    public int MaxConcurrency { get; set; } = 6;

    /// <summary>
    /// Per-request timeout. A timeout is a feed failure (HTTP 503), never a fallback.
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// Drops the blank and repeated entries from the three configured lists.
    /// <para>
    /// <c>ConfigurationBinder</c> populates an existing collection by <b>adding</b> to it, so
    /// binding the <c>MarketData</c> section onto this class yields the code defaults <em>plus</em>
    /// every entry written in <c>appsettings.json</c>. A section that repeats the defaults — the
    /// shipped file does — therefore doubles both lists: the index symbol is requested twice and
    /// the proxy reports 24 constituents for 12 names, which is exactly the number a failed run
    /// prints back to the operator. Normalising once at the options boundary keeps the run, the
    /// per-URL download cache and that diagnostic honest, and still lets an operator extend a
    /// list from configuration.
    /// </para>
    /// </summary>
    public void Normalize()
    {
        IndexYahooSymbols = DistinctNonBlank(IndexYahooSymbols);
        IndexProxyConstituents = DistinctNonBlank(IndexProxyConstituents);
        PseDailyQuotationUrlTemplates = DistinctNonBlank(PseDailyQuotationUrlTemplates);
    }

    /// <summary>
    /// The usable entries of a configured list: trimmed, non-blank and unique
    /// (case-insensitive, first occurrence wins, configured order preserved). Never null, so a
    /// caller can iterate the result without a guard.
    /// </summary>
    public static List<string> DistinctNonBlank(IEnumerable<string>? values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();

        foreach (var value in values ?? Array.Empty<string>())
        {
            var trimmed = value?.Trim();

            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
