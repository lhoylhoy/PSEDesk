using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Applies an official PSE Daily Quotation Report (downloaded by the operator, or fetched live)
/// onto an already-stored session. The report is the only public source of per-ticker and
/// market-wide net foreign flow, so this service owns activating the foreign pillar without
/// ever inventing a number: a ticker absent from the report keeps its existing value (usually
/// null = unreported), and the market total is only overwritten when the report states it.
/// </summary>
public sealed class DailyQuotationImportService
{
    /// <summary>Provenance recorded when an operator uploads the report through the API.</summary>
    public const string UploadSource = "pse-dqr-upload";

    private readonly IDailyQuotationParser _parser;
    private readonly ISignalRepository _repository;
    private readonly ILogger<DailyQuotationImportService> _logger;

    public DailyQuotationImportService(
        IDailyQuotationParser parser,
        ISignalRepository repository,
        ILogger<DailyQuotationImportService> logger)
    {
        _parser = parser;
        _repository = repository;
        _logger = logger;
    }

    /// <summary>
    /// Parses <paramref name="content"/> and rewrites the stored metrics for
    /// <paramref name="tradeDate"/> (or the report's own date when omitted). Foreign flows,
    /// suspension disclosures and the deterministic <see cref="StockDailyMetric.QuantPreScore"/>
    /// are recomputed; breadth and prices are left untouched.
    /// </summary>
    public async Task<QuotationReportImportResultDto> ApplyAsync(
        DateOnly? tradeDate,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        var report = _parser.Parse(fileName, content);
        if (!report.HasData)
        {
            throw new QuotationReportImportException(
                $"'{fileName}' did not contain any recognizable PSE foreign-flow rows. Upload the official " +
                "Daily Quotation Report as PDF/CSV, or a tab-delimited text export of it.");
        }

        var sessionDate = tradeDate ?? report.ReportDate
            ?? throw new QuotationReportImportException(
                "The report did not state its trading date. Pass the session date explicitly and retry.");

        var snapshot = await _repository.GetSnapshotAsync(sessionDate, cancellationToken)
            ?? throw new MarketSessionNotFoundException(
                $"No stored market session exists for {sessionDate:yyyy-MM-dd}. Run the signal pipeline for that " +
                "trading day first, then import the quotation report.");

        var metrics = await _repository.GetMetricsAsync(sessionDate, cancellationToken);
        if (metrics.Count == 0)
        {
            throw new MarketSessionNotFoundException(
                $"The stored session for {sessionDate:yyyy-MM-dd} has no ticker metrics to enrich.");
        }

        var suspended = new HashSet<string>(report.SuspendedTickers, StringComparer.OrdinalIgnoreCase);
        var matched = 0;
        var updated = 0;

        foreach (var metric in metrics)
        {
            var changed = false;

            if (report.ByTicker.TryGetValue(metric.Ticker, out var quote))
            {
                matched++;

                var netForeign = decimal.Round(quote.NetForeignPhp, 2, MidpointRounding.AwayFromZero);
                if (metric.NetForeignTodayPhp != netForeign)
                {
                    metric.NetForeignTodayPhp = netForeign;
                    changed = true;
                }
            }

            if (suspended.Contains(metric.Ticker) && EnsureSuspensionDisclosure(metric, sessionDate))
            {
                changed = true;
            }

            if (changed)
            {
                metric.QuantPreScore = QuantitativeFilterService.ComputePreScore(
                    metric.ClosePrice,
                    metric.Ema20,
                    metric.Sma50,
                    metric.Sma200,
                    metric.Rsi14,
                    metric.VolumeRatioVs20d,
                    metric.AvgTurnover20dPhp,
                    metric.NetForeignTodayPhp,
                    metric.Atr14,
                    metric.SupportLevel,
                    metric.ResistanceLevel);

                updated++;
            }
        }

        if (report.MarketNetForeignFlowPhp is null && matched == 0)
        {
            throw new QuotationReportImportException(
                $"'{fileName}' matched none of the {metrics.Count} stored tickers for {sessionDate:yyyy-MM-dd}. " +
                "Check that the file is the PSE Daily Quotation Report for that session.");
        }

        // Preserve every stored macro/breadth/rotation field and only overlay the foreign flow.
        var batch = MarketDataMapper.ToBatch(snapshot) with
        {
            NetForeignFlowPhp = report.MarketNetForeignFlowPhp ?? snapshot.NetForeignFlowPhp,
            ForeignFlowSource = report.MarketNetForeignFlowPhp is null
                ? snapshot.ForeignFlowSource
                : UploadSource
        };

        await _repository.SaveMarketDataAsync(batch, metrics, cancellationToken);

        _logger.LogInformation(
            "Imported {File} for {Date}: {Matched} matched tickers, {Updated} metrics updated, " +
            "{Suspended} suspended, market net foreign {Flow}.",
            fileName,
            sessionDate,
            matched,
            updated,
            suspended.Count,
            report.MarketNetForeignFlowPhp?.ToString("0.##") ?? "unreported");

        return new QuotationReportImportResultDto(
            sessionDate,
            UploadSource,
            batch.NetForeignFlowPhp,
            matched,
            updated,
            report.SuspendedTickers);
    }

    /// <summary>
    /// Adds the synthetic halt filing when the report lists the ticker as suspended. The
    /// deterministic classifier maps "suspended" onto <see cref="PseTrapFlag.TRADING_HALT_RISK"/>,
    /// which is a hard disqualifier for every horizon.
    /// </summary>
    private static bool EnsureSuspensionDisclosure(StockDailyMetric metric, DateOnly sessionDate)
    {
        var disclosures = MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson).ToList();

        var alreadyRecorded = disclosures.Any(disclosure =>
            disclosure.Date == sessionDate &&
            PseDisclosureClassifier.Classify(disclosure.Headline).Category == DisclosureCategory.TRADING_HALT);

        if (alreadyRecorded)
        {
            return false;
        }

        disclosures.Add(new StockDisclosure(
            sessionDate,
            $"{metric.Ticker} was listed as suspended in the official PSE Daily Quotation Report",
            "Disclosure",
            "PSE DQR"));

        metric.RecentDisclosuresJson = MarketDataMapper.SerializeDisclosures(disclosures);
        return true;
    }
}
