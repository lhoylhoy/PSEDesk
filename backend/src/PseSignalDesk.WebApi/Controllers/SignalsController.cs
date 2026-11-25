using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Application.Services;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Infrastructure.Scheduling;

namespace PseSignalDesk.WebApi.Controllers;

/// <summary>
/// REST surface for the research terminal: latest/historical lists, available dates,
/// per-ticker drift history and the two action endpoints.
/// </summary>
[ApiController]
[Route("api/signals")]
public sealed class SignalsController : ControllerBase
{
    private const long MaxQuotationReportBytes = 25 * 1024 * 1024;

    private static readonly string[] QuotationReportExtensions = { ".pdf", ".csv", ".txt", ".tsv" };

    private readonly ISignalRepository _repository;
    private readonly SignalPipelineService _pipeline;
    private readonly SignalOutcomeService _outcomes;
    private readonly BacktestService _backtests;
    private readonly PortfolioService _portfolio;
    private readonly DailyQuotationImportService _quotationImports;
    private readonly MoneyManagerTrainingService _training;
    private readonly ILogger<SignalsController> _logger;

    public SignalsController(
        ISignalRepository repository,
        SignalPipelineService pipeline,
        SignalOutcomeService outcomes,
        BacktestService backtests,
        PortfolioService portfolio,
        DailyQuotationImportService quotationImports,
        MoneyManagerTrainingService training,
        ILogger<SignalsController> logger)
    {
        _repository = repository;
        _pipeline = pipeline;
        _outcomes = outcomes;
        _backtests = backtests;
        _portfolio = portfolio;
        _quotationImports = quotationImports;
        _training = training;
        _logger = logger;
    }

    /// <summary>
    /// Latest generated list for the horizon. When a date only has market data the
    /// response still contains the regime strip plus a null run, so the UI can show a CTA.
    /// </summary>
    [HttpGet("latest")]
    public async Task<ActionResult<LatestSignalsResponse>> GetLatest(
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        CancellationToken cancellationToken = default)
    {
        horizon = NormalizeHorizon(horizon);

        var run = await _repository.GetLatestRunAsync(horizon, cancellationToken);
        var dates = await BuildDateOptionsAsync(horizon, cancellationToken);

        DateOnly? targetDate = run?.RunDate ?? (dates.Count > 0 ? dates[^1].Date : null);

        var snapshot = targetDate is null
            ? await _repository.GetLatestSnapshotAsync(cancellationToken)
            : await _repository.GetSnapshotAsync(targetDate.Value, cancellationToken);

        var metricsDate = run?.RunDate ?? snapshot?.TradeDate;
        var metrics = metricsDate is null
            ? Array.Empty<StockDailyMetric>()
            : await _repository.GetMetricsAsync(metricsDate.Value, cancellationToken);

        return Ok(await BuildEnvelopeAsync(run, snapshot, metrics, dates, horizon, cancellationToken));
    }

    /// <summary>
    /// Historical view for an explicit trading day. The run may be null when only the
    /// seeded market data exists for that date.
    /// </summary>
    [HttpGet("by-date")]
    public async Task<ActionResult<LatestSignalsResponse>> GetByDate(
        [FromQuery] string? date,
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseDate(date, out var runDate))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid date",
                Detail = "Pass date=YYYY-MM-DD, for example date=2026-11-24."
            });
        }

        horizon = NormalizeHorizon(horizon);

        var snapshot = await _repository.GetSnapshotAsync(runDate, cancellationToken);
        var run = await _repository.GetRunAsync(runDate, horizon, cancellationToken);

        if (snapshot is null && run is null)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "No data for trading day",
                Detail = $"No market snapshot or {horizon} signal run exists for {runDate:yyyy-MM-dd}. " +
                         "Run the live pipeline for that trading day first."
            });
        }

        var metrics = await _repository.GetMetricsAsync(runDate, cancellationToken);
        var dates = await BuildDateOptionsAsync(horizon, cancellationToken);

        return Ok(await BuildEnvelopeAsync(run, snapshot, metrics, dates, horizon, cancellationToken));
    }

    /// <summary>
    /// Distinct available trading days, merged from seeded snapshots and signal runs.
    /// </summary>
    [HttpGet("dates")]
    public async Task<ActionResult<IReadOnlyList<DateOptionDto>>> GetDates(
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        CancellationToken cancellationToken = default)
    {
        horizon = NormalizeHorizon(horizon);
        var dates = await BuildDateOptionsAsync(horizon, cancellationToken);
        return Ok(dates);
    }

    /// <summary>
    /// Causal walk-forward replay of the horizon's own sizing rules over the real tape: a book of
    /// up to five concurrent positions, 1% risk per trade inside a 6% portfolio heat cap, at most
    /// two names and 35% of equity per sector, board lots, fractional Kelly sizing, the full
    /// 0.690% round-trip friction plus slippage, and a 60-session walk-forward refit of the
    /// money-manager weights. The numbers describe one simulated path, not a promise.
    /// Returns 503 when the live feed has no history, never a synthetic curve.
    /// </summary>
    [HttpGet("backtest")]
    public async Task<ActionResult<BacktestResultDto>> RunBacktest(
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        [FromQuery] int? lookbackDays = null,
        [FromQuery] DateOnly? startDate = null,
        CancellationToken cancellationToken = default)
    {
        horizon = NormalizeHorizon(horizon);

        try
        {
            var result = await _backtests.RunAsync(
                horizon,
                ManilaTimeZone.TodayPht(),
                lookbackDays,
                startDate,
                cancellationToken);

            return Ok(result);
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning(ex, "Backtest for {Horizon} could not run", horizon);

            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Backtest data unavailable",
                detail: ex.Message);
        }
    }

    /// <summary>
    /// The desk's report card on its own money-manager models: one row per horizon with the
    /// held-out score, the promotion status and the ranked factor weights. Pass a horizon for
    /// just that book; omit it for all four.
    /// </summary>
    [HttpGet("model")]
    public async Task<ActionResult<IReadOnlyList<TrainedModelSummaryDto>>> GetModels(
        [FromQuery] SignalHorizon? horizon = null,
        CancellationToken cancellationToken = default)
    {
        if (horizon is not null)
        {
            return Ok(new[]
            {
                await _training.GetModelAsync(NormalizeHorizon(horizon.Value), cancellationToken)
            });
        }

        var summaries = new List<TrainedModelSummaryDto>(ProbabilisticEdgeService.AllHorizons.Count);

        foreach (var book in ProbabilisticEdgeService.AllHorizons)
        {
            summaries.Add(await _training.GetModelAsync(book, cancellationToken));
        }

        return Ok(summaries);
    }

    /// <summary>
    /// Fits the horizon's money-manager weights from the real PSE tape and persists them. The
    /// weights only go live when the held-out slice clears the sample gate and beats the
    /// neutral prior's Brier score; otherwise the response says so and the prior stays in
    /// charge. Answers 503 when the feed has no history — the desk never trains on synthetic data.
    /// </summary>
    [HttpPost("train")]
    public async Task<ActionResult<TrainedModelSummaryDto>> Train(
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        [FromQuery] int? lookbackDays = null,
        CancellationToken cancellationToken = default)
    {
        horizon = NormalizeHorizon(horizon);

        try
        {
            var summary = await _training.TrainAsync(
                horizon,
                ManilaTimeZone.TodayPht(),
                lookbackDays,
                cancellationToken);

            _logger.LogInformation(
                "Trained the {Horizon} money-manager model: status {Status}, Brier {Brier} vs {Baseline} baseline.",
                horizon,
                summary.Status,
                summary.TrainedBrierScore,
                summary.BaselineBrierScore);

            return Ok(summary);
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning(ex, "The {Horizon} money-manager model could not be trained", horizon);

            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Training data unavailable",
                detail: ex.Message);
        }
    }

    /// <summary>
    /// Fits all four books from one fetch and returns their report cards. Same promotion gate
    /// as the single-horizon endpoint.
    /// </summary>
    [HttpPost("train-all")]
    public async Task<ActionResult<IReadOnlyList<TrainedModelSummaryDto>>> TrainAll(
        [FromQuery] int? lookbackDays = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var summaries = await _training.TrainAllAsync(
                ManilaTimeZone.TodayPht(),
                lookbackDays,
                cancellationToken);

            return Ok(summaries);
        }
        catch (MarketDataUnavailableException ex)
        {
            _logger.LogWarning(ex, "The money-manager models could not be trained");

            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Training data unavailable",
                detail: ex.Message);
        }
    }

    /// <summary>
    /// What actually happened, in the desk's own words: realized win rate, expectancy,
    /// calibration bias, the sectors that paid, the factors the promoted model leans on and
    /// the latest stop-outs.
    /// </summary>
    [HttpGet("desk-memory")]
    public async Task<ActionResult<DeskMemorySummary>> GetDeskMemory(
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        CancellationToken cancellationToken = default)
    {
        horizon = NormalizeHorizon(horizon);

        return Ok(await _outcomes.BuildDeskMemoryAsync(horizon, cancellationToken));
    }

    /// <summary>
    /// Rank, conviction and price-level history for one ticker across past runs.
    /// </summary>
    [HttpGet("ticker/{symbol}/history")]
    public async Task<ActionResult<TickerHistoryDto>> GetTickerHistory(
        string symbol,
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        CancellationToken cancellationToken = default)
    {
        var ticker = (symbol ?? string.Empty).Trim().ToUpperInvariant();
        if (ticker.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid ticker",
                Detail = "Provide a PSE ticker such as ICT or BDO."
            });
        }

        horizon = NormalizeHorizon(horizon);
        var runs = await _repository.GetRunsForTickerAsync(ticker, horizon, cancellationToken);

        var points = runs
            .SelectMany(run => run.Items
                .Where(item => string.Equals(item.Ticker, ticker, StringComparison.OrdinalIgnoreCase))
                .Select(item => new TickerHistoryPointDto(
                    run.RunDate,
                    item.Rank,
                    item.DriftStatus.ToString(),
                    item.PreviousRank,
                    item.ConvictionScore,
                    item.EntryPrice,
                    item.StopLoss,
                    item.TargetPrice,
                    item.RiskRewardRatio,
                    item.NetRiskRewardRatio,
                    item.WinProbability,
                    item.ExpectedValueR,
                    item.EdgeVerdict.ToString(),
                    item.OutcomeStatus.ToString(),
                    item.RealizedNetR)))
            .OrderBy(point => point.RunDate)
            .ToList();

        if (points.Count == 0)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "No signal history",
                Detail = $"{ticker} has not appeared in any {horizon} run yet."
            });
        }

        var metric = await _repository.GetMetricAsync(points[^1].RunDate, ticker, cancellationToken);

        return Ok(new TickerHistoryDto(
            ticker,
            metric?.CompanyName ?? ticker,
            metric?.Sector ?? "PSE",
            points));
    }

    /// <summary>
    /// Forward-outcome scorecard for one horizon: how often the desk was right, what the
    /// average realized result was in R, the profit factor, the Brier calibration score and
    /// the TRADE / SMALL_SIZE / WATCH_ONLY breakdown. This is the desk grading itself.
    /// </summary>
    [HttpGet("scorecard")]
    public async Task<ActionResult<SignalScorecardDto>> GetScorecard(
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        CancellationToken cancellationToken = default)
    {
        horizon = NormalizeHorizon(horizon);
        var scorecard = await _outcomes.BuildScorecardAsync(horizon, cancellationToken);
        return Ok(scorecard);
    }

    /// <summary>
    /// Runs the live quant filter + signal engine for a date (defaults to the latest
    /// stored session, otherwise today in PHT). Market data is fetched from Yahoo
    /// Finance and PSE Edge; there is no demo seed and no offline engine.
    /// </summary>
    [HttpPost("run-now")]
    public async Task<ActionResult<SignalRunDto>> RunNow(
        [FromQuery] string? date = null,
        [FromQuery] SignalHorizon horizon = SignalHorizon.WEEK,
        CancellationToken cancellationToken = default)
    {
        horizon = NormalizeHorizon(horizon);

        DateOnly runDate;

        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!TryParseDate(date, out runDate))
            {
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid date",
                    Detail = "Pass date=YYYY-MM-DD, for example date=2026-11-24."
                });
            }
        }
        else
        {
            // The latest stored session is not "now": asking for it by default re-ran the same
            // frozen day and left the desk showing yesterday's tape. A run without a date means
            // today in Manila; the provider resolves the real session and the pipeline re-dates
            // the run when the exchange was closed.
            runDate = ManilaTimeZone.TodayPht();
        }

        try
        {
            var run = await _pipeline.RunAsync(runDate, horizon, cancellationToken);
            return Ok(run);
        }
        catch (OllamaConfigurationException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Ollama Cloud is not configured",
                detail: ex.Message);
        }
        catch (MarketDataUnavailableException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "Market data unavailable",
                detail: ex.Message);
        }
        catch (SignalGenerationException ex)
        {
            _logger.LogWarning(ex, "Signal generation failed for {Date}.", runDate);
            return Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Signal generation failed",
                detail: ex.Message);
        }
    }

    /// <summary>
    /// Imports the official PSE Daily Quotation Report for a stored session and overlays the
    /// per-ticker net foreign flow plus suspension disclosures. Provenance is recorded as
    /// pse-dqr-upload so an operator upload never masquerades as a live fetch.
    /// </summary>
    [HttpPost("quotation-report")]
    [RequestSizeLimit(MaxQuotationReportBytes)]
    public async Task<ActionResult<QuotationReportImportResultDto>> ImportQuotationReport(
        [FromForm] IFormFile? file,
        [FromForm] string? date,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Missing file",
                Detail = "Attach the PSE Daily Quotation Report as multipart form data in the 'file' field " +
                         "(PDF, CSV, TXT or TSV)."
            });
        }

        if (file.Length > MaxQuotationReportBytes)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "File too large",
                Detail = "The quotation-report upload limit is 25 MB."
            });
        }

        var extension = Path.GetExtension(file.FileName);

        if (!QuotationReportExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Unsupported file type",
                Detail = $"'{extension}' is not a quotation-report format. Upload a PDF, CSV, TXT or TSV export."
            });
        }

        DateOnly? sessionDate = null;

        if (!string.IsNullOrWhiteSpace(date))
        {
            if (!TryParseDate(date, out var parsedDate))
            {
                return BadRequest(new ProblemDetails
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "Invalid date",
                    Detail = "Pass date=YYYY-MM-DD, for example date=2026-11-24."
                });
            }

            sessionDate = parsedDate;
        }

        byte[] content;

        using (var buffer = new MemoryStream())
        {
            await file.CopyToAsync(buffer, cancellationToken);
            content = buffer.ToArray();
        }

        try
        {
            var result = await _quotationImports.ApplyAsync(sessionDate, file.FileName, content, cancellationToken);
            return Ok(result);
        }
        catch (QuotationReportImportException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Quotation report could not be applied",
                Detail = ex.Message
            });
        }
        catch (MarketSessionNotFoundException ex)
        {
            return NotFound(new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "No stored session for that trading day",
                Detail = ex.Message
            });
        }
    }

    private async Task<IReadOnlyList<DateOptionDto>> BuildDateOptionsAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        var snapshotDates = (await _repository.GetSnapshotDatesAsync(cancellationToken)).ToHashSet();
        var runDates = (await _repository.GetRunDatesAsync(horizon, cancellationToken)).ToHashSet();

        return snapshotDates
            .Union(runDates)
            .OrderBy(date => date)
            .Select(date => new DateOptionDto(date, snapshotDates.Contains(date), runDates.Contains(date)))
            .ToList();
    }

    private async Task<LatestSignalsResponse> BuildEnvelopeAsync(
        SignalRun? run,
        DailyMarketSnapshot? snapshot,
        IReadOnlyList<StockDailyMetric> metrics,
        IReadOnlyList<DateOptionDto> dates,
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        var metricsByTicker = metrics.ToDictionary(m => m.Ticker, StringComparer.OrdinalIgnoreCase);

        // Order tickets are sized against the desk's own equity and heat, so the list can
        // render "Take Trade" numbers without the browser ever doing the arithmetic. One book
        // read feeds every pick, and each pick is sized for its own sector and pyramid state.
        var book = await _portfolio.GetBookSizingSnapshotAsync(cancellationToken);
        var runDto = run is null ? null : SignalRunMapper.ToDto(run, metricsByTicker, book);

        // The trust pulse is the desk's own report card. A ledger read must never take the
        // whole terminal down, so any failure other than cancellation hides the pulse
        // instead of failing the response — a beginner should never see a 500 for honesty.
        TrustPulseDto? trustPulse = null;

        try
        {
            var scorecard = await _outcomes.BuildScorecardAsync(horizon, cancellationToken);
            trustPulse = MarketDirectionService.ToTrustPulse(scorecard);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Trust pulse for {Horizon} could not be built", horizon);
        }

        return new LatestSignalsResponse(
            runDto,
            snapshot is null ? null : MarketDataMapper.ToRegimeDto(snapshot, run),
            dates,
            runDto?.Drops ?? Array.Empty<SignalDropDto>(),
            trustPulse);
    }

    private static SignalHorizon NormalizeHorizon(SignalHorizon horizon) =>
        Enum.IsDefined(horizon) ? horizon : SignalHorizon.WEEK;

    private static bool TryParseDate(string? value, out DateOnly date) =>
        DateOnly.TryParseExact(
            (value ?? string.Empty).Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
}

