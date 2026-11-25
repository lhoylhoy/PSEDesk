namespace PseSignalDesk.Application.Models;

/// <summary>
/// One cash dividend event parsed from the Yahoo Finance chart
/// <c>events.dividends</c> block. <see cref="ExDate"/> is the exchange ex-date; the
/// amount is per share in PHP.
/// </summary>
public sealed record StockDividendEvent(DateOnly ExDate, decimal AmountPerShare);
