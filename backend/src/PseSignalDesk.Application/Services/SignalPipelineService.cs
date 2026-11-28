using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Orchestrates one daily signal run: market data, quant filtering, LLM ranking,
/// deterministic post-processing and an atomic persistence step.
/// </summary>
public sealed class SignalPipelineService
{
    /// <summary>
    /// Notional position budget used to translate a plan into whole board lots for the
    /// UI. It is a display convention, not a recommendation: the desk never tells a user
    /// how much of their own capital to commit.
    /// </summary>
    public const decimal DefaultPositionBudgetPhp = 100_000m;

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IMarketDataProvider _marketDataProvider;
    private readonly QuantitativeFilterService _quantFilter;
    private readonly IOllamaSignalEngine _signalEngine;
    private readonly ISignalRepository _repository;
    private readonly ILogger<SignalPipelineService> _logger;
    private readonly IKnowledgeRetrievalService? _knowledge;
    private readonly ProbabilisticEdgeService _edge;
    private readonly SignalOutcomeService _outcomes;
    private readonly PortfolioService? _portfolio;

    public SignalPipelineService(
        IMarketDataProvider marketDataProvider,
        QuantitativeFilterService quantFilter,
        IOllamaSignalEngine signalEngine,
        ISignalRepository repository,
        ILogger<SignalPipelineService> logger,
        IKnowledgeRetrievalService? knowledge = null,
        ProbabilisticEdgeService? edgeService = null,
        SignalOutcomeService? outcomeService = null,
        PortfolioService? portfolioService = null)
    {
        _marketDataProvider = marketDataProvider;
        _quantFilter = quantFilter;
        _signalEngine = signalEngine;
        _repository = repository;
        _logger = logger;
        _knowledge = knowledge;
        _edge = edgeService ?? new ProbabilisticEdgeService();
        _outcomes = outcomeService ?? new SignalOutcomeService(repository);
        _portfolio = portfolioService;
    }

    public async Task<SignalRunDto> RunAsync(
        DateOnly runDate,
        SignalHorizon horizon,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await _repository.GetSnapshotAsync(runDate, cancellationToken);
        IReadOnlyList<StockDailyMetric> metrics = snapshot is null
            ? Array.Empty<StockDailyMetric>()
            : await _repository.GetMetricsAsync(runDate, cancellationToken);
        var freshFetch = false;
        IReadOnlyDictionary<string, BarrierStatistics>? barrierStatsByTicker = null;

        if (snapshot is null || metrics.Count == 0)
        {
            var batch = await _marketDataProvider.FetchAsync(runDate, cancellationToken);
            if (batch.Stocks.Count == 0)
            {
                throw new MarketDataUnavailableException(
                    $"No live PSE market data is available for {runDate:yyyy-MM-dd}. Try another trading day once the exchange session has closed.");
            }

            // The feed dates the batch by the session it actually returned (the newest PSEI bar),
            // not by the calendar day that was requested. A Saturday request is served by Friday's
            // close, so the run, the snapshot and every downstream grade must carry Friday's date;
            // otherwise a run is stored under a day that never traded and the drift chain breaks.
            if (batch.TradeDate != runDate)
            {
                _logger.LogWarning(
                    "Requested the {Requested:yyyy-MM-dd} session but the live feed returned {Session:yyyy-MM-dd}; the run is dated by the exchange session.",
                    runDate,
                    batch.TradeDate);

                runDate = batch.TradeDate;
            }

            metrics = _quantFilter.EvaluateBatch(batch);
            if (metrics.Count == 0)
            {
                throw new MarketDataUnavailableException(
                    $"None of the {batch.Stocks.Count} live tickers had enough history to compute the 200-day trend for {runDate:yyyy-MM-dd}.");
            }

            var fetchProfile = HorizonProfile.For(horizon);
            barrierStatsByTicker = batch.Stocks
                .Where(stock => stock.Bars.Count >= ProbabilisticEdgeService.MinimumHistoryBars)
                .ToDictionary(
                    stock => stock.Ticker,
                    stock => ProbabilisticEdgeService.ComputeBarrierStatistics(stock.Bars, fetchProfile),
                    StringComparer.OrdinalIgnoreCase);

            // Attribution before disqualification: the market-wide Edge page has no company
            // column, so the screened names are re-read through their own company listing. This
            // is what puts a rights offering or a tender offer in front of the trap screen
            // instead of leaving it dependent on a headline that names its issuer.
            metrics = await EnrichCompanyDisclosuresAsync(runDate, horizon, metrics, cancellationToken);

            // A one-day Edge page rolls over. Merge what is stored for the tickers into today's
            // filings before persisting, so an active rights offering or tender offer keeps
            // disqualifying the name for the following sessions.
            metrics = await MergeRollingDisclosuresAsync(runDate, metrics, cancellationToken);
            freshFetch = true;

            // Breadth is derived from the metrics that were just computed, so the snapshot,
            // the prompt and every pick in this run share one number.
            var breadth = QuantitativeFilterService.ComputeMarketBreadth(metrics);
            batch = batch with
            {
                Breadth = breadth,
                SectorRotation = SectorRotationService.Compute(metrics)
            };

            await _repository.SaveMarketDataAsync(batch, metrics, cancellationToken);
            snapshot = MarketDataMapper.ToSnapshot(batch);

            _logger.LogInformation(
                "Market data for {Date} stored from {Source} ({Tickers} tickers, {Metrics} metrics).",
                runDate,
                batch.Source,
                batch.Stocks.Count,
                metrics.Count);
        }

        if (!freshFetch)
        {
            // Stored sessions keep the full rolling history on their own rows; rebuild the view
            // so an old session still shows the filings that were in force on that day.
            metrics = await MergeRollingDisclosuresAsync(runDate, metrics, cancellationToken);
        }

        var profile = HorizonProfile.For(horizon);

        // The persisted row keeps the full 120-day history. Each book only sees its own window,
        // so a DAY run is not blocked by a rights offering that expired a month ago, while a
        // YEAR run still carries the whole quarter's corporate actions.
        var horizonMetrics = metrics
            .Select(metric => metric.WithDisclosures(MarketDataMapper.SerializeDisclosures(
                QuantitativeFilterService.ScopeDisclosures(
                    MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson),
                    runDate,
                    profile.DisclosureLookbackDays))))
            .ToList();

        // A stored session may predate breadth tracking; recompute it from its own metrics
        // so the prompt and the UI never fall back to a blank tape.
        var computedBreadth = QuantitativeFilterService.ComputeMarketBreadth(metrics);
        snapshot.BreadthAboveSma50Pct ??= computedBreadth.AboveSma50Pct;
        snapshot.BreadthAboveSma200Pct ??= computedBreadth.AboveSma200Pct;
        snapshot.Stage2Count ??= computedBreadth.Stage2Count;
        snapshot.EvaluatedStockCount ??= computedBreadth.EvaluatedStockCount;

        // Sector rotation is a pure function of today's metrics, so it is always recomputed
        // for the run and only persisted when the stored session has no rotation yet. That
        // keeps a re-run of an old session consistent with its own metrics.
        var sectorRotation = SectorRotationService.Compute(metrics);
        snapshot.SectorRotationJson ??= MarketDataMapper.SerializeSectorRotation(sectorRotation);

        var marketBreadth = new MarketBreadth(
            snapshot.BreadthAboveSma50Pct,
            snapshot.BreadthAboveSma200Pct,
            snapshot.Stage2Count ?? 0,
            snapshot.EvaluatedStockCount ?? metrics.Count);

        // Before grading today's list, grade every earlier pick that now has forward
        // history. A grading failure must never block a live run.
        try
        {
            var graded = await _outcomes.ResolveOpenSignalsAsync(horizon, cancellationToken);

            if (graded > 0)
            {
                _logger.LogInformation("Graded {Count} open {Horizon} picks against the new session.", graded, horizon);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Forward-outcome grading failed before the {Horizon} run; the ledger stays open.", horizon);
        }

        var candidates = _quantFilter.SelectCandidates(horizonMetrics, horizon, QuantitativeFilterService.MaxCandidates);
        if (candidates.Count == 0)
        {
            throw new MarketDataUnavailableException(
                "No ticker cleared the PHP 5,000,000 average 20-day turnover floor and the structural trap " +
                "screen (rights offerings, tender offers, trading halts) for this trading day.");
        }

        var previousRun = await _repository.GetPreviousRunAsync(runDate, horizon, cancellationToken);
        var playbooks = await RetrievePlaybooksAsync(horizon, candidates, cancellationToken);
        var dataQualityWarnings = BuildDataQualityWarnings(snapshot, horizonMetrics);
        var request = BuildRequest(
            snapshot,
            horizon,
            candidates,
            previousRun,
            playbooks,
            marketBreadth,
            sectorRotation,
            await LoadDeskMemoryAsync(horizon, cancellationToken),
            await LoadTrainedModelSummaryAsync(horizon, cancellationToken),
            dataQualityWarnings);
        var result = await _signalEngine.GenerateSignalAsync(request, cancellationToken);

        var run = BuildRun(
            runDate,
            horizon,
            horizonMetrics,
            candidates,
            previousRun,
            result.Output,
            playbooks,
            snapshot.MarketRegime,
            marketBreadth,
            sectorRotation,
            snapshot.Us10YClosePct,
            snapshot.NetForeignFlowPhp,
            await LoadLearnedEdgeModelAsync(horizon, cancellationToken),
            await LoadCalibrationOffsetPctAsync(horizon, cancellationToken),
            barrierStatsByTicker);

        // Provenance travels with the book: which model, which prompt, which sampling settings
        // and what it cost. Without it a ranking can never be compared with another one.
        ApplyModelProvenance(run, result.Metadata);
        await _repository.SaveSignalRunAsync(run, cancellationToken);

        _logger.LogInformation(
            "Signal run {Date} {Horizon} completed with {Engine}: {Items} picks and {Drops} drops.",
            runDate,
            horizon,
            _signalEngine.EngineName,
            run.Items.Count,
            run.Drops.Count);

        var metricsByTicker = horizonMetrics.ToDictionary(m => m.Ticker, StringComparer.OrdinalIgnoreCase);

        // The run response carries order tickets sized against the desk's own book. A book
        // failure (empty account, storage hiccup) must never break a completed signal run, so
        // the tickets are best-effort and simply absent when the context cannot be built.
        BookSizingSnapshot? book = null;
        if (_portfolio is not null)
        {
            try
            {
                book = await _portfolio.GetBookSizingSnapshotAsync(cancellationToken, horizon);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Order-ticket context unavailable for this run; the picks carry no ticket.");
            }
        }

        return SignalRunMapper.ToDto(run, metricsByTicker, book, dataQualityWarnings);
    }

    public static IReadOnlyList<string> BuildDataQualityWarnings(
        DailyMarketSnapshot? snapshot,
        IReadOnlyList<StockDailyMetric> metrics)
    {
        var warnings = new List<string>(3);

        if (snapshot is not null && (snapshot.PseiClose <= 0m || snapshot.PseiSma50 <= 0m))
        {
            warnings.Add(
                "PSEi level or moving averages are unreported (proxy fallback active); treat index trend signals cautiously.");
        }

        if (snapshot is not null && snapshot.NetForeignFlowPhp is null)
        {
            warnings.Add(
                "Official PSE Daily Quotation Report foreign flow is unreported for this session; do not infer foreign buying or selling.");
        }

        if (metrics.Count > 0 &&
            metrics.All(metric => MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson).Count == 0))
        {
            warnings.Add(
                "Zero PSE Edge disclosures were attributed this session; disclosure-based trap flags are unenforced.");
        }

        return warnings;
    }

    /// <summary>
    /// Pulls the mandatory PSE guardrails plus the best horizon/sector/trap-matched
    /// playbook sections for this run. Retrieval is an enhancement: if the knowledge base
    /// is unavailable the run continues ungrounded rather than failing a live signal.
    /// </summary>
    private async Task<IReadOnlyList<KnowledgeSnippet>> RetrievePlaybooksAsync(
        SignalHorizon horizon,
        IReadOnlyList<StockDailyMetric> candidates,
        CancellationToken cancellationToken)
    {
        if (_knowledge is null)
        {
            return Array.Empty<KnowledgeSnippet>();
        }

        var profile = HorizonProfile.For(horizon);
        var tickers = string.Join(", ", candidates.Take(8).Select(c => $"{c.Ticker} ({c.Sector})"));
        var trapFlags = candidates
            .SelectMany(candidate => PseDisclosureClassifier.DetectTrapFlags(
                MarketDataMapper.DeserializeDisclosures(candidate.RecentDisclosuresJson)))
            .Distinct()
            .ToList();

        var query = new KnowledgeQuery(
            $"{profile.Mission} Candidates: {tickers}.",
            horizon,
            candidates.Count > 0 ? candidates[0].Sector : null,
            trapFlags);

        try
        {
            var snippets = await _knowledge.RetrieveAsync(query, cancellationToken);

            _logger.LogInformation(
                "Retrieved {Snippets} knowledge-base sections for the {Horizon} run.",
                snippets.Count,
                horizon);

            return snippets;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Knowledge-base retrieval failed for the {Horizon} run; the prompt will carry no playbook citations.",
                horizon);
            return Array.Empty<KnowledgeSnippet>();
        }
    }

    private static OllamaSignalRequest BuildRequest(
        DailyMarketSnapshot snapshot,
        SignalHorizon horizon,
        IReadOnlyList<StockDailyMetric> candidates,
        SignalRun? previousRun,
        IReadOnlyList<KnowledgeSnippet> playbooks,
        MarketBreadth breadth,
        IReadOnlyList<SectorRotationDto> sectorRotation,
        DeskMemorySummary? deskMemory = null,
        TrainedModelSummaryDto? learnedModel = null,
        IReadOnlyList<string>? dataQualityWarnings = null)
    {
        var profile = HorizonProfile.For(horizon);
        var yesterday = previousRun is null
            ? new List<PriorSignalInput>()
            : previousRun.Items
                .OrderBy(i => i.Rank)
                .Select(i => new PriorSignalInput(
                    i.Ticker,
                    i.Rank,
                    i.ConvictionScore,
                    i.EntryPrice,
                    i.StopLoss,
                    i.TargetPrice,
                    i.Thesis))
                .ToList();

        return new OllamaSignalRequest(
            snapshot.TradeDate,
            horizon,
            snapshot.MarketRegime,
            snapshot.RegimeNote,
            snapshot.PseiClose,
            snapshot.NetForeignFlowPhp,
            snapshot.UsdPhp,
            candidates.Select(candidate => MarketDataMapper.ToCandidate(
                candidate,
                SectorRotationService.QuadrantFor(sectorRotation, candidate.Sector),
                profile)).ToList(),
            yesterday,
            playbooks,
            Breadth: breadth,
            SectorRotation: sectorRotation,
            Us10YClosePct: snapshot.Us10YClosePct,
            Us10YChange20dBps: snapshot.Us10YChange20dBps,
            BrentUsd: snapshot.BrentUsd,
            BrentChange20dPct: snapshot.BrentChange20dPct,
            EpheCloseUsd: snapshot.EpheCloseUsd,
            EpheChange20dPct: snapshot.EpheChange20dPct,
            ForeignFlowSource: snapshot.ForeignFlowSource,
            DeskMemory: deskMemory,
            LearnedModel: learnedModel,
            DataQualityWarnings: dataQualityWarnings);
    }

    /// <summary>
    /// Re-reads each screened name's disclosures from the exchange's per-company listing and folds
    /// them into the metric that will be screened and persisted.
    /// <para>
    /// The market-wide page carries no company column, so a filing whose subject does not name its
    /// issuer cannot be attributed at all — and the structural trap screen (rights offering,
    /// tender offer, halt) then passes a name it should have rejected, without an error anywhere.
    /// The fan-out is bounded by the provider (one GET per name, at most
    /// <see cref="QuantitativeFilterService.MaxCandidates"/> of them) and entirely best-effort:
    /// a provider that cannot answer leaves the market-wide attribution in place.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<StockDailyMetric>> EnrichCompanyDisclosuresAsync(
        DateOnly runDate,
        SignalHorizon horizon,
        IReadOnlyList<StockDailyMetric> metrics,
        CancellationToken cancellationToken)
    {
        try
        {
            var tickers = _quantFilter.SelectDisclosureFanout(
                metrics,
                horizon,
                QuantitativeFilterService.MaxCandidates);

            if (tickers.Count == 0)
            {
                return metrics;
            }

            var attributed = await _marketDataProvider.FetchCompanyDisclosuresAsync(
                tickers,
                runDate,
                cancellationToken);

            if (attributed.Count == 0)
            {
                return metrics;
            }

            return metrics
                .Select(metric => attributed.TryGetValue(metric.Ticker, out var filings) && filings.Count > 0
                    ? metric.WithDisclosures(MarketDataMapper.SerializeDisclosures(
                        QuantitativeFilterService.MergeRollingDisclosures(
                            MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson),
                            filings,
                            runDate)))
                    : metric)
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "The per-company disclosure lookup failed for the {Horizon} run; the market-wide scrape stays in force.",
                horizon);

            return metrics;
        }
    }


    /// <summary>
    /// Builds the rolling disclosure view for this run. The stored ticker rows over the
    /// maximum window are folded into today's filings, deduplicated and capped. History is an
    /// enhancement: a repository failure logs and leaves the day's own filings in place.
    /// </summary>
    private async Task<IReadOnlyList<StockDailyMetric>> MergeRollingDisclosuresAsync(
        DateOnly runDate,
        IReadOnlyList<StockDailyMetric> metrics,
        CancellationToken cancellationToken)
    {
        if (metrics.Count == 0)
        {
            return metrics;
        }

        try
        {
            var history = await _repository.GetMetricsRangeAsync(
                runDate.AddDays(-QuantitativeFilterService.MaxRollingDisclosureLookbackDays),
                cancellationToken);

            var historyByTicker = history
                .Where(metric => metric.TradeDate < runDate)
                .GroupBy(metric => metric.Ticker, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (IReadOnlyList<StockDisclosure>)group
                        .SelectMany(metric => MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson))
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase);

            return metrics
                .Select(metric => metric.WithDisclosures(MarketDataMapper.SerializeDisclosures(
                    QuantitativeFilterService.MergeRollingDisclosures(
                        MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson),
                        historyByTicker.GetValueOrDefault(metric.Ticker),
                        runDate))))
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Rolling disclosure history unavailable for {Date}; the run sees only that session's filings.",
                runDate);

            return metrics;
        }
    }

    /// <summary>
    /// The desk's own track record for the prompt. A repository failure means the prompt simply
    /// omits the block instead of claiming a record the desk cannot prove.
    /// </summary>
    private async Task<DeskMemorySummary?> LoadDeskMemoryAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        try
        {
            var memory = await _outcomes.BuildDeskMemoryAsync(horizon, cancellationToken);

            return memory.ResolvedCount == 0 ? null : memory;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "{Horizon}: the desk's own track record could not be read; the prompt omits it.",
                horizon);

            return null;
        }
    }

    /// <summary>
    /// The model's public report card for the prompt: status, held-out score and top factors.
    /// </summary>
    private async Task<TrainedModelSummaryDto?> LoadTrainedModelSummaryAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        try
        {
            var model = await _repository.GetTrainedModelAsync(horizon, cancellationToken);

            return model is null ? null : TrainedModelPresenter.ToDto(model, horizon.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "{Horizon}: the trained model summary could not be read; the prompt omits it.",
                horizon);

            return null;
        }
    }

    /// <summary>
    /// Loads the promoted money-manager model for the horizon. No model, or one that failed
    /// its held-out gate, is the normal case; a repository failure must never block a live
    /// run, so it degrades to the deterministic prior and says so in the log.
    /// </summary>
    private async Task<LearnedEdgeModel?> LoadLearnedEdgeModelAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        try
        {
            var model = await _repository.GetTrainedModelAsync(horizon, cancellationToken);
            var learned = LearnedEdgeModel.FromActive(model);

            if (learned is not null)
            {
                _logger.LogInformation(
                    "{Horizon}: pricing this run with the learned money-manager model (held-out Brier {Brier} vs {Baseline} baseline).",
                    horizon,
                    model!.TrainedBrierScore,
                    model.BaselineBrierScore);
            }

            return learned;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "{Horizon}: the trained model could not be read, so the structural prior prices this run.",
                horizon);

            return null;
        }
    }

    /// <summary>
    /// The desk's own forecast error for this book, in probability points. Zero until enough
    /// picks have been graded, and never able to block a run.
    /// </summary>
    private async Task<decimal> LoadCalibrationOffsetPctAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _outcomes.ComputeLiveCalibrationOffsetPctAsync(horizon, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "{Horizon}: the live calibration offset could not be computed, so the odds stay uncalibrated.",
                horizon);

            return 0m;
        }
    }

    private SignalRun BuildRun(
        DateOnly runDate,
        SignalHorizon horizon,
        IReadOnlyList<StockDailyMetric> metrics,
        IReadOnlyList<StockDailyMetric> candidates,
        SignalRun? previousRun,
        OllamaSignalOutput output,
        IReadOnlyList<KnowledgeSnippet> playbooks,
        MarketRegime regime,
        MarketBreadth breadth,
        IReadOnlyList<SectorRotationDto> sectorRotation,
        decimal? us10YClosePct,
        decimal? marketNetForeignFlowPhp,
        LearnedEdgeModel? learnedModel,
        decimal calibrationOffsetPct,
        IReadOnlyDictionary<string, BarrierStatistics>? barrierStatsByTicker = null)
    {
        var profile = HorizonProfile.For(horizon);
        var metricByTicker = metrics.ToDictionary(m => m.Ticker, StringComparer.OrdinalIgnoreCase);
        var candidateByTicker = candidates.ToDictionary(c => c.Ticker, StringComparer.OrdinalIgnoreCase);

        // Preserve the exact horizon-adjusted screen order (ComputeHorizonScore, then turnover,
        // then ticker) that the prompt prints, so QuantRank agrees with the table the model saw.
        var quantRankByTicker = candidates
            .Select((candidate, index) => (candidate.Ticker, Rank: index + 1))
            .ToDictionary(entry => entry.Ticker, entry => entry.Rank, StringComparer.OrdinalIgnoreCase);
        var priorByTicker = previousRun?.Items.ToDictionary(i => i.Ticker, StringComparer.OrdinalIgnoreCase)
                            ?? new Dictionary<string, SignalItem>(StringComparer.OrdinalIgnoreCase);
        var bearCaseByTicker = (output.Top10 ?? new List<OllamaTopPick>())
            .Where(pick => !string.IsNullOrWhiteSpace(pick.BearCase))
            .GroupBy(pick => (pick.Ticker ?? string.Empty).Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().BearCase!.Trim(), StringComparer.OrdinalIgnoreCase);

        var usedTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SignalItem>();

        foreach (var pick in output.Top10 ?? new List<OllamaTopPick>())
        {
            if (items.Count >= 10)
            {
                break;
            }

            var ticker = (pick.Ticker ?? string.Empty).Trim().ToUpperInvariant();
            if (ticker.Length == 0 || usedTickers.Contains(ticker))
            {
                continue;
            }

            if (!candidateByTicker.TryGetValue(ticker, out var candidate))
            {
                _logger.LogWarning("Discarding {Ticker} from the model response: not in the liquid candidate set.", ticker);
                continue;
            }

            if (string.IsNullOrWhiteSpace(pick.Thesis))
            {
                _logger.LogWarning("Discarding {Ticker} from the model response: the pick has no thesis.", ticker);
                continue;
            }

            if (priorByTicker.TryGetValue(ticker, out var priorItem) &&
                IsResolvedPosition(candidate, priorItem, out var hitTarget))
            {
                _logger.LogInformation(
                    "Excluding {Ticker}: yesterday's {Level} was already reached ({Close}).",
                    ticker,
                    hitTarget ? "target" : "stop",
                    candidate.ClosePrice);
                continue;
            }

            // DAY ideas must live inside the exchange's static +/- band, so they are
            // clamped before the tick grid is applied. Longer horizons keep the model's
            // levels but the violations are still audited into the log.
            var plan = profile.Horizon == SignalHorizon.DAY
                ? PseBoardLotService.NormalizeTradePlanWithinStaticLimits(
                    candidate.ClosePrice,
                    pick.EntryPrice,
                    pick.StopLoss,
                    pick.TargetPrice)
                : PseBoardLotService.NormalizeTradePlan(pick.EntryPrice, pick.StopLoss, pick.TargetPrice);

            foreach (var violation in PseBoardLotService.ValidateAgainstStaticThresholds(
                         candidate.ClosePrice,
                         plan.Entry,
                         plan.StopLoss,
                         plan.Target))
            {
                _logger.LogWarning("{Ticker}: {Violation}", ticker, violation);
            }

            if (profile.RequireNetRewardRisk)
            {
                var netRiskReward = PseBoardLotService.ComputeNetRiskReward(plan.Entry, plan.StopLoss, plan.Target);
                if (netRiskReward < profile.MinimumNetRiskReward)
                {
                    _logger.LogInformation(
                        "Discarding {Ticker}: net reward-to-risk {NetRiskReward} after round-trip fees is below the " +
                        "{Horizon} gate of {Gate}.",
                        ticker,
                        netRiskReward,
                        horizon,
                        profile.MinimumNetRiskReward);
                    continue;
                }
            }

            usedTickers.Add(ticker);
            items.Add(CreateItem(
                candidate,
                items.Count + 1,
                pick.ConvictionScore,
                plan,
                pick.Thesis,
                pick.DeviationReason,
                priorByTicker,
                BuildCitations(pick.CitedRules, playbooks),
                quantRankByTicker.TryGetValue(ticker, out var quantRank) ? quantRank : null));
        }

        if (items.Count == 0)
        {
            throw new SignalGenerationException(
                "The model response did not contain any tradeable ticker from the liquid candidate set. " +
                "No quant-only picks are added: the buy list is either model-ranked or it fails.");
        }

        // The probability, the expected value, the Quarter-Kelly stake, the verdict and the
        // pre-mortem are all server-owned: the model never gets to write its own odds.
        foreach (var item in items)
        {
            var metric = metricByTicker[item.Ticker];
            var sectorQuadrant = SectorRotationService.QuadrantFor(sectorRotation, metric.Sector);
            var tickSpreadPct = item.EntryPrice > 0m
                ? PseBoardLotService.ComputeTickSpreadRate(item.EntryPrice) * 100m
                : 0m;

            // The desk sizes its published clips against the default notional budget, so the
            // probability is graded on the same all-in fee rate (0.690% + spread + impact) the
            // ticket will charge instead of the fee-only round trip.
            var effectiveFeeRate = PseBoardLotService.ComputeEffectiveRoundTripFeeRate(
                item.EntryPrice,
                DefaultPositionBudgetPhp,
                metric.AvgTurnover20dPhp,
                metric.AtrPct);

            // Net foreign flow expressed against the name's own 20-day turnover: the desk's
            // scale-free read on whether institutions were accumulating or distributing.
            var foreignTurnoverRatio = metric.NetForeignTodayPhp is decimal netForeign &&
                                       metric.AvgTurnover20dPhp > 0m
                ? netForeign / metric.AvgTurnover20dPhp
                : (decimal?)null;

            BarrierStatistics? barrierStats = null;
            if (barrierStatsByTicker is not null &&
                barrierStatsByTicker.TryGetValue(item.Ticker, out var resolvedStats) &&
                resolvedStats.Trials > 0)
            {
                barrierStats = resolvedStats;
            }

            var assessment = _edge.EvaluateSetup(new SetupEdgeRequest(
                item.EntryPrice,
                item.StopLoss,
                item.TargetPrice,
                item.ConvictionScore,
                regime,
                profile,
                // The pick is graded against its own book's base rate, not the WEEK rate.
                metric.EmpiricalWinRateFor(horizon),
                metric.RelativeStrength63d,
                metric.VolatilityContractionRatio,
                metric.WeinsteinStage,
                breadth.AboveSma50Pct,
                PseDisclosureClassifier.DetectTrapFlags(
                    MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson)),
                metric.AtrPct,
                metric.TrailingDividendYieldPct,
                sectorQuadrant,
                tickSpreadPct,
                metric.UpDownVolumeRatio20d,
                CalibrationOffsetPct: calibrationOffsetPct,
                SlippageInclusiveFeeRate: effectiveFeeRate,
                NetForeignTurnoverRatio: foreignTurnoverRatio,
                Us10YClosePct: us10YClosePct,
                Sector: metric.Sector,
                LearnedModel: learnedModel,
                LearnedFeatures: TrainedModelFeatures.FromMetric(metric),
                BarrierStats: barrierStats));

            item.NetRiskRewardRatio = assessment.NetRiskReward;
            item.WinProbability = assessment.WinProbability;
            item.BreakEvenWinRate = assessment.BreakEvenWinRate;
            item.ExpectedValueR = assessment.ExpectedValueR;
            item.QuarterKellyFraction = assessment.QuarterKellyFraction;
            item.EdgeVerdict = assessment.Verdict;
            item.BearCase = ResolveBearCase(
                bearCaseByTicker.GetValueOrDefault(item.Ticker),
                item,
                assessment,
                metric);

            if (item.EdgeVerdict == EdgeVerdict.WATCH_ONLY)
            {
                _logger.LogInformation(
                    "{Ticker}: WATCH_ONLY. Expected value {ExpectedValueR}R at {WinProbability:P0} win probability " +
                    "against a {BreakEven:P0} break-even rate.",
                    item.Ticker,
                    item.ExpectedValueR,
                    item.WinProbability,
                    item.BreakEvenWinRate);
            }
        }

        var tradeCount = items.Count(item => item.EdgeVerdict == EdgeVerdict.TRADE);
        var smallSizeCount = items.Count(item => item.EdgeVerdict == EdgeVerdict.SMALL_SIZE);

        var drops = BuildDrops(previousRun, items, metricByTicker, output);

        return new SignalRun
        {
            RunDate = runDate,
            Horizon = horizon,
            CreatedAtUtc = DateTime.UtcNow,
            RawModelResponseJson = JsonSerializer.Serialize(output, AuditJsonOptions),
            Items = items,
            Drops = drops,
            RecommendedCashPct = ProbabilisticEdgeService.ComputeRecommendedCashPct(
                regime,
                breadth.AboveSma50Pct,
                tradeCount,
                smallSizeCount,
                marketNetForeignFlowPhp),
            BreadthAboveSma50Pct = breadth.AboveSma50Pct,
            BreadthAboveSma200Pct = breadth.AboveSma200Pct,
            Stage2Count = breadth.Stage2Count,
            EvaluatedStockCount = breadth.EvaluatedStockCount
        };
    }

    /// <summary>
    /// The pre-mortem is mandatory. The model's own words are kept when they are short
    /// enough to be actionable; otherwise the server states the invalidation level and the
    /// overhead supply in one deterministic sentence.
    /// </summary>
    private static string ResolveBearCase(
        string? modelBearCase,
        SignalItem item,
        EdgeAssessment assessment,
        StockDailyMetric metric)
    {
        var trimmed = (modelBearCase ?? string.Empty).Trim();

        if (trimmed.Length > 0 && trimmed.Length <= 200 && trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 30)
        {
            return trimmed;
        }

        var resistance = metric.ResistanceLevel > 0m
            ? PseBoardLotService.RoundToValidTick(metric.ResistanceLevel)
            : item.TargetPrice;
        var stage = string.IsNullOrWhiteSpace(metric.WeinsteinStage) ? "trend" : metric.WeinsteinStage.Replace('_', ' ');

        return
            $"A close below PHP {FormatMoney(item.StopLoss)} invalidates the {stage.ToLowerInvariant()} setup — sell there to limit losses. " +
            $"Sellers near PHP {FormatMoney(resistance)} may block a rebound, and the win probability of {assessment.WinProbability:P0} " +
            "is only a shrunk estimate, not a promise.";
    }

    /// <summary>
    /// A pick whose prior stop or target has already been touched is a closed trade:
    /// it is excluded from today's buy list (the drop is classified as INVALIDATED or
    /// TARGET_HIT) instead of being re-recommended.
    /// </summary>
    private static bool IsResolvedPosition(
        StockDailyMetric metric,
        SignalItem prior,
        out bool hitTarget)
    {
        hitTarget = false;

        var hasSanePlan = prior.TargetPrice > prior.EntryPrice && prior.StopLoss < prior.EntryPrice;
        if (!hasSanePlan)
        {
            return false;
        }

        if (metric.ClosePrice >= prior.TargetPrice)
        {
            hitTarget = true;
            return true;
        }

        return metric.ClosePrice <= prior.StopLoss;
    }

    /// <summary>
    /// Copies the engine's provenance onto the run before it is persisted, so the stored book
    /// always says how it was produced. Kept separate from <see cref="BuildRun"/> because the
    /// run's content must be a pure function of the server inputs and the parsed output.
    /// </summary>
    private static void ApplyModelProvenance(SignalRun run, OllamaGenerationMetadata metadata)
    {
        run.EngineName = metadata.EngineName;
        run.ModelName = metadata.Model;
        run.PromptHash = metadata.PromptHash;
        run.ModelTemperature = metadata.Temperature;
        run.ModelSeed = metadata.Seed;
        run.ModelRetryCount = metadata.RetryCount;
        run.ModelLatencyMs = metadata.LatencyMs;
        run.PromptTokens = metadata.PromptTokens;
        run.CompletionTokens = metadata.CompletionTokens;
        run.PromptCharacters = metadata.PromptCharacters;
        run.SchemaConstrained = metadata.SchemaConstrained;
        run.FastExtractionApplied = metadata.FastExtractionApplied;
    }

    private static SignalItem CreateItem(
        StockDailyMetric metric,
        int rank,
        decimal convictionScore,
        (decimal Entry, decimal StopLoss, decimal Target) plan,
        string thesis,
        string? deviationReason,
        IReadOnlyDictionary<string, SignalItem> priorByTicker,
        IReadOnlyList<string> citations,
        int? quantRank = null)
    {
        priorByTicker.TryGetValue(metric.Ticker, out var prior);

        var drift = prior is null
            ? DriftStatus.NEW
            : rank < prior.Rank
                ? DriftStatus.UPGRADED
                : rank > prior.Rank
                    ? DriftStatus.DOWNGRADED
                    : DriftStatus.HELD;

        return new SignalItem
        {
            Rank = rank,
            Ticker = metric.Ticker,
            DriftStatus = drift,
            PreviousRank = prior?.Rank,
            ConvictionScore = (int)Math.Clamp(convictionScore, 0m, 100m),
            QuantPreScore = metric.QuantPreScore,
            QuantRank = quantRank,
            EntryPrice = plan.Entry,
            StopLoss = plan.StopLoss,
            TargetPrice = plan.Target,
            RiskRewardRatio = PseBoardLotService.ComputeRiskReward(plan.Entry, plan.StopLoss, plan.Target),
            Thesis = thesis.Trim(),
            DeviationReason = BuildDeviationReason(metric, rank, drift, prior, deviationReason),
            BoardLotShares = ResolveBoardLotShares(plan.Entry),
            CitedReferencesJson = citations.Count == 0
                ? "[]"
                : JsonSerializer.Serialize(citations, AuditJsonOptions)
        };
    }

    /// <summary>
    /// Whole board lots for the desk's notional budget, falling back to one exchange board
    /// lot (the smallest order that can actually be placed) when the budget is too small.
    /// </summary>
    private static int ResolveBoardLotShares(decimal entryPrice)
    {
        var shares = PseBoardLotService.RoundSharesToBoardLot(DefaultPositionBudgetPhp, entryPrice);

        return shares > 0 ? shares : PseBoardLotService.GetBoardLotSize(entryPrice);
    }

    /// <summary>
    /// Keeps only citations that point at a section the retrieval actually supplied, so a
    /// model can never cite a fabricated rule.
    /// </summary>
    private static IReadOnlyList<string> BuildCitations(
        IReadOnlyList<string>? citedRules,
        IReadOnlyList<KnowledgeSnippet> playbooks)
    {
        if (citedRules is null || citedRules.Count == 0 || playbooks.Count == 0)
        {
            return Array.Empty<string>();
        }

        var allowedReferences = playbooks.Select(p => p.Reference).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowedSlugs = playbooks.Select(p => p.DocumentSlug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var citations = new List<string>();

        foreach (var raw in citedRules)
        {
            var citation = (raw ?? string.Empty).Trim();
            if (citation.Length == 0)
            {
                continue;
            }

            if (!allowedReferences.Contains(citation) && !allowedSlugs.Contains(citation))
            {
                continue;
            }

            if (!citations.Contains(citation, StringComparer.OrdinalIgnoreCase))
            {
                citations.Add(citation);
            }

            if (citations.Count == 4)
            {
                break;
            }
        }

        return citations;
    }

    private static List<SignalDrop> BuildDrops(
        SignalRun? previousRun,
        IReadOnlyList<SignalItem> chosenItems,
        IReadOnlyDictionary<string, StockDailyMetric> metricByTicker,
        OllamaSignalOutput output)
    {
        var drops = new List<SignalDrop>();

        if (previousRun is null || previousRun.Items.Count == 0)
        {
            return drops;
        }

        var chosenTickers = new HashSet<string>(chosenItems.Select(i => i.Ticker), StringComparer.OrdinalIgnoreCase);
        var modelDrops = new Dictionary<string, (DropCategory? Category, string Reason)>(StringComparer.OrdinalIgnoreCase);

        foreach (var drop in output.DroppedFromYesterday ?? new List<OllamaDrop>())
        {
            var ticker = (drop.Ticker ?? string.Empty).Trim().ToUpperInvariant();
            if (ticker.Length == 0 || modelDrops.ContainsKey(ticker))
            {
                continue;
            }

            DropCategory? parsedCategory = null;
            if (Enum.TryParse<DropCategory>(drop.DropCategory, ignoreCase: true, out var category) &&
                Enum.IsDefined(category))
            {
                parsedCategory = category;
            }

            modelDrops[ticker] = (parsedCategory, (drop.Reason ?? string.Empty).Trim());
        }

        foreach (var prior in previousRun.Items.OrderBy(i => i.Rank))
        {
            if (chosenTickers.Contains(prior.Ticker))
            {
                continue;
            }

            metricByTicker.TryGetValue(prior.Ticker, out var metric);
            modelDrops.TryGetValue(prior.Ticker, out var modelDrop);

            DropCategory resolvedCategory;
            string reason;

            if (metric is not null && metric.ClosePrice <= prior.StopLoss)
            {
                resolvedCategory = DropCategory.INVALIDATED;
                reason =
                    $"Closed PHP {FormatMoney(metric.ClosePrice)} at or below the prior stop of " +
                    $"PHP {FormatMoney(prior.StopLoss)}, so the setup failed and the cash stays safe instead.";
            }
            else if (metric is not null && metric.ClosePrice >= prior.TargetPrice)
            {
                resolvedCategory = DropCategory.TARGET_HIT;
                reason =
                    $"Closed PHP {FormatMoney(metric.ClosePrice)} at or above the prior target of " +
                    $"PHP {FormatMoney(prior.TargetPrice)}, so the profit target was reached and the trade was recycled.";
            }
            else if (modelDrop.Category is not null)
            {
                resolvedCategory = modelDrop.Category.Value;
                reason = string.IsNullOrWhiteSpace(modelDrop.Reason)
                    ? $"Left yesterday's rank #{prior.Rank} and was reclassified as {resolvedCategory}."
                    : modelDrop.Reason;
            }
            else
            {
                resolvedCategory = DropCategory.DISPLACED;
                reason =
                    $"A stronger setup took its place on today's list after the screening; " +
                    $"{prior.Ticker} was ranked #{prior.Rank} yesterday.";
            }

            if (modelDrop.Category == resolvedCategory && !string.IsNullOrWhiteSpace(modelDrop.Reason))
            {
                reason = modelDrop.Reason;
            }

            drops.Add(new SignalDrop
            {
                Ticker = prior.Ticker,
                PreviousRank = prior.Rank,
                DropCategory = resolvedCategory,
                Reason = reason
            });
        }

        return drops;
    }

    private static string BuildDeviationReason(
        StockDailyMetric metric,
        int rank,
        DriftStatus drift,
        SignalItem? prior,
        string? modelDeviationReason)
    {
        if (!string.IsNullOrWhiteSpace(modelDeviationReason))
        {
            return modelDeviationReason.Trim();
        }

        var volumeRatio = FormatMoney(metric.VolumeRatioVs20d);
        var rsi = FormatMoney(metric.Rsi14);

        return drift switch
        {
            DriftStatus.NEW =>
                $"New entrant: cleared the PHP 5M liquidity floor with a {metric.QuantPreScore}/100 quant pre-score, " +
                $"{volumeRatio}x 20-day volume and RSI {rsi}.",
            DriftStatus.UPGRADED =>
                $"Upgraded from #{prior?.Rank} to #{rank}: relative conviction improved as trend alignment and " +
                $"volume expansion strengthened with RSI at {rsi}.",
            DriftStatus.DOWNGRADED =>
                $"Slipped from #{prior?.Rank} to #{rank}: relative conviction faded against higher-scoring " +
                "candidates even though the broader structure remains valid.",
            _ =>
                $"Held at #{rank}: trend alignment and liquidity remain intact with RSI at {rsi} and a " +
                $"{metric.QuantPreScore}/100 quant pre-score."
        };
    }

    /// <summary>
    /// Invariant-culture decimal formatting for server-generated sentences, so a
    /// machine with a comma decimal separator never produces "PHP 1,5" style text.
    /// </summary>
    private static string FormatMoney(decimal value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);
}


