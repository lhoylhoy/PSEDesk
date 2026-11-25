namespace PseSignalDesk.Application.Models;

/// <summary>
/// Raw per-ticker input for the quantitative engine: price history plus the
/// market-level context (foreign flow and disclosures) that is not derivable
/// from candles alone.
/// </summary>
public sealed record StockMarketData(
    string Ticker,
    string CompanyName,
    string Sector,
    IReadOnlyList<OhlcvBar> Bars,
    decimal? NetForeignTodayPhp,
    IReadOnlyList<StockDisclosure> Disclosures,
    IReadOnlyList<StockDividendEvent>? Dividends = null)
{
    public OhlcvBar? LatestBar => Bars.Count == 0 ? null : Bars[^1];
}
