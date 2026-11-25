using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Infrastructure.MarketData;
using PseSignalDesk.Infrastructure.Persistence;

namespace PseSignalDesk.Tests.Application;

/// <summary>
/// The upload path is the desk's manual bridge to the official PSE Daily Quotation Report, so it
/// runs against a real SQLite session: the stored snapshot must keep every macro dial while the
/// foreign pillar is switched on with the upload provenance.
/// </summary>
public sealed class DailyQuotationImportServiceTests : IDisposable
{
    private static readonly DateOnly TradeDate = new(2026, 11, 24);

    private readonly SqliteConnection _connection;
    private readonly PseSignalDbContext _db;
    private readonly SignalRepository _repository;
    private readonly DailyQuotationImportService _service;

    public DailyQuotationImportServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new PseSignalDbContext(
            new DbContextOptionsBuilder<PseSignalDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        _repository = new SignalRepository(_db);
        _service = new DailyQuotationImportService(
            new PseDailyQuotationParser(
                new HashSet<string>(new[] { "ICT", "TEL", "BDO" }, StringComparer.OrdinalIgnoreCase)),
            _repository,
            NullLogger<DailyQuotationImportService>.Instance);

        SeedSession();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task ApplyAsync_overlays_the_foreign_pillar_and_keeps_the_stored_macro_dials()
    {
        var csv = string.Join('\n', new[]
        {
            "Ticker,Security Name,Close,Value,Net Foreign",
            "ICT,Intl Container,430.00,1250000000,25000000",
            "TEL,PLDT,1300.00,0,SUSPENDED",
            "Net Foreign Total,,,,30000000"
        });

        var result = await _service.ApplyAsync(
            TradeDate,
            "dqr-20261124.csv",
            Encoding.UTF8.GetBytes(csv));

        Assert.Equal(DailyQuotationImportService.UploadSource, result.Source);
        Assert.Equal(30_000_000m, result.MarketNetForeignFlowPhp!.Value);
        // TEL only contributes a halt flag, not a flow, so it is suspended rather than matched.
        Assert.Equal(1, result.MatchedTickerCount);
        Assert.Equal(2, result.UpdatedMetricCount);
        Assert.Equal(new[] { "TEL" }, result.SuspendedTickers);

        var snapshot = await _repository.GetSnapshotAsync(TradeDate);
        Assert.NotNull(snapshot);
        Assert.Equal(30_000_000m, snapshot!.NetForeignFlowPhp!.Value);
        Assert.Equal("pse-dqr-upload", snapshot.ForeignFlowSource);

        // Untouched dials survive the re-save: an upload only owns the foreign flow.
        Assert.Equal(4.2m, snapshot.Us10YClosePct!.Value);
        Assert.Equal(72.4m, snapshot.BrentUsd!.Value);
        Assert.Equal(62m, snapshot.BreadthAboveSma50Pct!.Value);

        var metrics = (await _repository.GetMetricsAsync(TradeDate)).ToDictionary(
            metric => metric.Ticker,
            StringComparer.OrdinalIgnoreCase);

        Assert.Equal(25_000_000m, metrics["ICT"].NetForeignTodayPhp!.Value);
        Assert.Null(metrics["BDO"].NetForeignTodayPhp);
        Assert.Contains(
            "suspended",
            metrics["TEL"].RecentDisclosuresJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            PseTrapFlag.TRADING_HALT_RISK,
            PseDisclosureClassifier.Classify(
                "TEL was listed as suspended in the official PSE Daily Quotation Report").TrapFlag);
    }

    [Fact]
    public async Task ApplyAsync_recomputes_the_pre_score_for_the_updated_metrics_only()
    {
        var csv = "Ticker,Close,Value,Net Foreign\nICT,430.00,1250000000,25000000";

        await _service.ApplyAsync(TradeDate, "dqr.csv", Encoding.UTF8.GetBytes(csv));

        var metrics = (await _repository.GetMetricsAsync(TradeDate)).ToDictionary(
            metric => metric.Ticker,
            StringComparer.OrdinalIgnoreCase);

        var ict = metrics["ICT"];
        var expected = QuantitativeFilterService.ComputePreScore(
            ict.ClosePrice,
            ict.Ema20,
            ict.Sma50,
            ict.Sma200,
            ict.Rsi14,
            ict.VolumeRatioVs20d,
            ict.AvgTurnover20dPhp,
            ict.NetForeignTodayPhp,
            ict.Atr14,
            ict.SupportLevel,
            ict.ResistanceLevel);

        Assert.Equal(expected, ict.QuantPreScore);
        Assert.Null(metrics["BDO"].NetForeignTodayPhp);
    }

    [Fact]
    public async Task ApplyAsync_refuses_a_session_that_was_never_stored()
    {
        var csv = "Ticker,Close,Value,Net Foreign\nICT,430.00,1250000000,25000000";

        var error = await Assert.ThrowsAsync<MarketSessionNotFoundException>(() =>
            _service.ApplyAsync(TradeDate.AddDays(1), "dqr.csv", Encoding.UTF8.GetBytes(csv)));

        Assert.Contains("No stored market session", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ApplyAsync_rejects_a_file_with_no_recognizable_rows()
    {
        var error = await Assert.ThrowsAsync<QuotationReportImportException>(() =>
            _service.ApplyAsync(null, "notes.csv", Encoding.UTF8.GetBytes("lunch at noon")));

        Assert.Contains(
            "did not contain any recognizable PSE foreign-flow rows",
            error.Message,
            StringComparison.Ordinal);
    }

    private void SeedSession()
    {
        _db.DailyMarketSnapshots.Add(new DailyMarketSnapshot
        {
            TradeDate = TradeDate,
            PseiClose = 8_514.08m,
            PseiSma50 = 8_400m,
            PseiSma200 = 8_100m,
            NetForeignFlowPhp = null,
            UsdPhp = 58.27m,
            MarketRegime = MarketRegime.BULLISH,
            RegimeNote = "PSEi above its 50D and 200D averages.",
            BreadthAboveSma50Pct = 62m,
            Us10YClosePct = 4.2m,
            BrentUsd = 72.4m,
            ForeignFlowSource = null
        });

        _db.StockDailyMetrics.AddRange(
            BuildMetric("ICT", 430m, 250_000_000m),
            BuildMetric("TEL", 1_300m, 90_000_000m),
            BuildMetric("BDO", 150m, 400_000_000m));

        _db.SaveChanges();
        _db.ChangeTracker.Clear();
    }

    private static StockDailyMetric BuildMetric(string ticker, decimal close, decimal turnover) => new()
    {
        TradeDate = TradeDate,
        Ticker = ticker,
        CompanyName = $"{ticker} Holdings",
        Sector = "Industrial",
        ClosePrice = close,
        ChangePct = 1.2m,
        AvgTurnover20dPhp = turnover,
        VolumeRatioVs20d = 1.1m,
        Rsi14 = 55m,
        Ema20 = decimal.Round(close * 0.99m, 2),
        Sma50 = decimal.Round(close * 0.97m, 2),
        Sma200 = decimal.Round(close * 0.92m, 2),
        Atr14 = decimal.Round(close * 0.02m, 2),
        SupportLevel = decimal.Round(close * 0.98m, 2),
        ResistanceLevel = decimal.Round(close * 1.04m, 2),
        NetForeignTodayPhp = null,
        QuantPreScore = 70,
        RecentDisclosuresJson = "[]"
    };
}
