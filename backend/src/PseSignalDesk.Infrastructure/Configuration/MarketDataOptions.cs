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
    /// PSE Edge company-disclosure search page. It is polled alongside the announcement page and
    /// is best-effort: a failure there never blocks a run, because the announcement page is the
    /// contract.
    /// </summary>
    public string PseEdgeCompanyDisclosuresUrl { get; set; } =
        "https://edge.pse.com.ph/companyDisclosures/search.ax";

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
}
