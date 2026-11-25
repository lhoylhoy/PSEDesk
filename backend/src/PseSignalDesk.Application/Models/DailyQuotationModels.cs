namespace PseSignalDesk.Application.Models;

/// <summary>
/// One ticker's foreign-flow line from an official PSE Daily Quotation Report. The net
/// figure is signed: positive is net foreign buying, negative is net foreign selling.
/// <see cref="ClosePrice"/> and <see cref="ValueTurnoverPhp"/> are optional because the CSV
/// export and the PDF layout do not always carry every column.
/// </summary>
public sealed record PseForeignFlowQuote(
    string Ticker,
    decimal NetForeignPhp,
    decimal? ClosePrice = null,
    decimal? ValueTurnoverPhp = null);

/// <summary>
/// Deterministic parse of one PSE Daily Quotation Report (PDF, CSV or tab-delimited text).
/// An empty <see cref="ByTicker"/> with a null <see cref="MarketNetForeignFlowPhp"/> means the
/// file was unrelated or unreadable; callers must report that instead of storing zeros.
/// </summary>
public sealed record ParsedDailyQuotationReport(
    DateOnly? ReportDate,
    decimal? MarketNetForeignFlowPhp,
    IReadOnlyDictionary<string, PseForeignFlowQuote> ByTicker,
    IReadOnlyList<string> SuspendedTickers)
{
    public static ParsedDailyQuotationReport Empty { get; } = new(
        ReportDate: null,
        MarketNetForeignFlowPhp: null,
        ByTicker: new Dictionary<string, PseForeignFlowQuote>(StringComparer.OrdinalIgnoreCase),
        SuspendedTickers: Array.Empty<string>());

    public bool HasData => ByTicker.Count > 0 || MarketNetForeignFlowPhp is not null;
}

/// <summary>
/// Result of applying an uploaded Daily Quotation Report onto a stored session.
/// </summary>
public sealed record QuotationReportImportResultDto(
    DateOnly TradeDate,
    string Source,
    decimal? MarketNetForeignFlowPhp,
    int MatchedTickerCount,
    int UpdatedMetricCount,
    IReadOnlyList<string> SuspendedTickers);
