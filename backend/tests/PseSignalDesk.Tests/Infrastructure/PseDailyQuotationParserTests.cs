using System.Text;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Infrastructure.MarketData;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The DQR parser is the only path that may turn the exchange's own report into foreign-flow
/// dials, so these tests pin the contract: accounting negatives parse, suspended names never
/// become flows, and an unrelated file yields an empty report instead of zeros.
/// </summary>
public sealed class PseDailyQuotationParserTests
{
    private static readonly IReadOnlySet<string> Tracked =
        new HashSet<string>(new[] { "ICT", "SM", "BDO", "TEL", "JFC" }, StringComparer.OrdinalIgnoreCase);

    private readonly PseDailyQuotationParser _parser = new(Tracked);

    [Fact]
    public void Parse_reads_a_csv_export_by_header_name_and_keeps_the_sign()
    {
        var csv = string.Join('\n', new[]
        {
            "PSE Daily Quotation Report",
            "11/24/2026",
            "Ticker,Security Name,Close,Value,Net Foreign",
            "ICT,Intl Container,430.00,\"1,250,000,000.00\",\"(12,500,000.00)\"",
            "SM,SM Investments,850.00,900000000,22500000",
            "BDO,BDO Unibank,150.00,400000000,-",
            "TEL,PLDT,1300.00,0,SUSPENDED",
            "Net Foreign Total,,,,40000000"
        });

        var report = _parser.Parse("dqr-20261124.csv", Encoding.UTF8.GetBytes(csv));

        Assert.True(report.HasData);
        Assert.Equal(new DateOnly(2026, 11, 24), report.ReportDate);
        Assert.Equal(-12_500_000m, report.ByTicker["ICT"].NetForeignPhp);
        Assert.Equal(22_500_000m, report.ByTicker["SM"].NetForeignPhp);
        Assert.Equal(430m, report.ByTicker["ICT"].ClosePrice!.Value);
        Assert.Equal(40_000_000m, report.MarketNetForeignFlowPhp);
        Assert.Equal(new[] { "TEL" }, report.SuspendedTickers);

        // A dash is unreported, never a zero-flow print.
        Assert.False(report.ByTicker.ContainsKey("BDO"));
    }

    [Fact]
    public void Parse_reads_a_tab_delimited_export()
    {
        var tsv = string.Join('\n', new[]
        {
            "Ticker\tClose\tValue\tNet Foreign\tStatus",
            "ICT\t430.00\t1,250,000,000.00\t(12,500,000.00)\t",
            "JFC\t210.00\t300,000,000.00\t8,000,000.00\tSuspended"
        });

        var report = _parser.Parse("dqr.tsv", Encoding.UTF8.GetBytes(tsv));

        Assert.Equal(-12_500_000m, report.ByTicker["ICT"].NetForeignPhp);
        Assert.DoesNotContain("JFC", report.ByTicker.Keys);
        Assert.Equal(new[] { "JFC" }, report.SuspendedTickers);
    }

    [Fact]
    public void Parse_reads_the_whitespace_layout_and_the_suspended_block()
    {
        var text = string.Join('\n', new[]
        {
            "PSE DAILY QUOTATION REPORT",
            "November 24, 2026",
            "Ticker Close Value Net Foreign",
            "ICT 430.00 1,250,000,000.00 12,500,000.00",
            "BDO 150.00 400,000,000.00 (12,000,000.00)",
            "SUSPENDED",
            "TEL"
        });

        var report = _parser.Parse("dqr.txt", Encoding.UTF8.GetBytes(text));

        Assert.Equal(new DateOnly(2026, 11, 24), report.ReportDate);
        Assert.Equal(12_500_000m, report.ByTicker["ICT"].NetForeignPhp);
        Assert.Equal(-12_000_000m, report.ByTicker["BDO"].NetForeignPhp);
        Assert.Equal(new[] { "TEL" }, report.SuspendedTickers);
        Assert.Null(report.MarketNetForeignFlowPhp);
    }

    [Fact]
    public void Parse_keeps_a_missing_marker_in_its_own_column()
    {
        // The exchange blanks the turnover but still prints the flow. A parser that compacts the
        // row would read 25,000,000 as turnover and report the flow as unreported.
        var csv = string.Join('\n', new[]
        {
            "Ticker,Security Name,Close,Value,Net Foreign",
            "ICT,Intl Container,430.00,-,25000000",
            "SM,SM Investments,850.00,900000000,N/A",
            "BDO,BDO Unibank,150.00,\"1,000,000.00\",\"(2,500,000.00)\""
        });

        var report = _parser.Parse("dqr-20261124.csv", Encoding.UTF8.GetBytes(csv));

        Assert.Equal(25_000_000m, report.ByTicker["ICT"].NetForeignPhp);
        Assert.Null(report.ByTicker["ICT"].ValueTurnoverPhp);
        Assert.Equal(-2_500_000m, report.ByTicker["BDO"].NetForeignPhp);

        // An N/A flow is unreported: the row must not borrow the turnover figure beside it.
        Assert.False(report.ByTicker.ContainsKey("SM"));
    }

    [Fact]
    public void Parse_returns_an_empty_report_for_unrelated_or_broken_content()
    {
        Assert.False(_parser.Parse("empty.csv", Array.Empty<byte>()).HasData);
        Assert.False(_parser.Parse("notes.txt", Encoding.UTF8.GetBytes("lunch at 12")).HasData);

        // A .pdf extension with no readable PDF body must degrade to "unreported", not throw.
        var brokenPdf = Encoding.ASCII.GetBytes("%PDF-1.7 not really a document");
        Assert.False(_parser.Parse("dqr.pdf", brokenPdf).HasData);
    }

    [Fact]
    public void Parse_ignores_tickers_outside_the_tracked_universe()
    {
        var csv = string.Join('\n', new[]
        {
            "Ticker,Close,Value,Net Foreign",
            "ZZZZ,1.00,1000,500",
            "ICT,430.00,1250000000,25000000"
        });

        var report = _parser.Parse("dqr.csv", Encoding.UTF8.GetBytes(csv));

        var quote = Assert.Single(report.ByTicker.Values);
        Assert.Equal("ICT", quote.Ticker);
    }
}
