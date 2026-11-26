using PseSignalDesk.Application.Models;

namespace PseSignalDesk.Application.Interfaces;

/// <summary>
/// Supplies a complete trading day (candles, foreign flow, disclosures, PSEi
/// context). Implementations must return a self-contained batch because the
/// database is the only cache used by the pipeline.
/// </summary>
public interface IMarketDataProvider
{
    Task<MarketDataBatch> FetchAsync(DateOnly tradeDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the same session with an explicit calendar lookback so the walk-forward
    /// backtester can request ~730 days (~500 PSE sessions). Implementations that cannot
    /// widen the window fall back to the configured lookback.
    /// </summary>
    Task<MarketDataBatch> FetchAsync(
        DateOnly tradeDate,
        int? lookbackDaysOverride,
        CancellationToken cancellationToken = default) =>
        FetchAsync(tradeDate, cancellationToken);

    /// <summary>
    /// Drops any cached upstream responses so the next fetch reflects the live tape. A manual
    /// run calls this when the operator asks for fresh numbers; scheduled runs rely on the
    /// provider's short time-to-live instead. Providers that cache nothing keep this no-op.
    /// </summary>
    void InvalidateCache() { }

    /// <summary>
    /// Fetches a single <c>{SYMBOL}.PS</c> chart plus its disclosures on demand, so the
    /// research endpoint can inspect any PSE name — including tickers that are not in the
    /// tracking catalog. Returning null means the symbol has no usable series.
    /// </summary>
    Task<StockMarketData?> FetchSingleStockAsync(
        string symbol,
        DateOnly tradeDate,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not support single-symbol research fetches.");

    /// <summary>
    /// Whether the symbol belongs to the desk's tracked catalog, as opposed to a name that
    /// is only reachable through an on-demand research fetch. Providers that keep no
    /// catalog answer false, which is the honest "not confirmed" reply for the UI.
    /// </summary>
    bool IsTracked(string symbol) => false;

    /// <summary>
    /// The tracked catalog (ticker, company name, sector) so the research endpoint can resolve
    /// a familiar company name to its PSE symbol and the UI can offer company-name search. It
    /// is static metadata — never a price, size or probability. Providers that keep no catalog
    /// return an empty list and research stays ticker-only.
    /// </summary>
    IReadOnlyList<CatalogStockDto> GetCatalog() => Array.Empty<CatalogStockDto>();

    /// <summary>
    /// The PSEi session series, so single-name research can price relative strength and beta
    /// without refetching the whole market. A provider that cannot serve it returns an empty
    /// series, and the research reports those factors as unreported instead of guessing.
    /// </summary>
    Task<IReadOnlyList<OhlcvBar>> FetchIndexBarsAsync(
        DateOnly tradeDate,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OhlcvBar>>(Array.Empty<OhlcvBar>());

    /// <summary>
    /// Re-reads disclosures for specific companies through the exchange's <b>per-company</b>
    /// listing, so a filing is attributed by the exchange's own company identifier instead of by
    /// matching its headline to a company name.
    /// <para>
    /// This is a second, bounded pass rather than part of <see cref="FetchAsync"/>: the caller
    /// knows which names survived the liquidity screen, and a walk-forward replay that fetches two
    /// years of tape must not pay for a fan-out it does not use. The result is keyed by ticker and
    /// contains only names that actually have filings; a failed lookup leaves that name on the
    /// market-wide scrape. Providers that cannot resolve companies return an empty map.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>> FetchCompanyDisclosuresAsync(
        IReadOnlyList<string> tickers,
        DateOnly sessionDate,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<StockDisclosure>>>(
            new Dictionary<string, IReadOnlyList<StockDisclosure>>(StringComparer.OrdinalIgnoreCase));
}
