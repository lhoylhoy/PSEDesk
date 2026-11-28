using Microsoft.Extensions.Logging;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Offline causal benchmark harness that replays sampled historical sessions through
/// <see cref="IOllamaSignalEngine"/> and grades the LLM's picks side-by-side against the
/// deterministic Quant Top-10 baseline on real forward OHLC bars.
/// </summary>
public sealed class PromptEvalService
{
    public const int DefaultSessions = 5;
    public const int MaxSessions = 20;
    public const int DefaultLookbackCalendarDays = 730;

    private readonly IMarketDataProvider _marketData;
    private readonly QuantitativeFilterService _quantFilter;
    private readonly IOllamaSignalEngine _signalEngine;
    private readonly ProbabilisticEdgeService _edge;
    private readonly IKnowledgeRetrievalService? _knowledge;
    private readonly ILogger<PromptEvalService>? _logger;

    public PromptEvalService(
        IMarketDataProvider marketData,
        QuantitativeFilterService quantFilter,
        IOllamaSignalEngine signalEngine,
        ProbabilisticEdgeService? edge = null,
        IKnowledgeRetrievalService? knowledge = null,
        ILogger<PromptEvalService>? logger = null)
    {
        _marketData = marketData;
        _quantFilter = quantFilter;
        _signalEngine = signalEngine;
        _edge = edge ?? new ProbabilisticEdgeService();
        _knowledge = knowledge;
        _logger = logger;
    }

    public async Task<PromptEvalResultDto> EvaluateAsync(
        SignalHorizon horizon,
        int sessions,
        DateOnly asOfDate,
        int? lookbackDays = null,
        CancellationToken cancellationToken = default)
    {
        var requestedSessions = Math.Clamp(sessions, 1, MaxSessions);
        var profile = HorizonProfile.For(horizon);
        var resolvedLookback = Math.Max(
            QuantitativeFilterService.MinBarsRequired + profile.ForwardWindowBars + 60,
            lookbackDays ?? DefaultLookbackCalendarDays);

        var batch = await _marketData.FetchAsync(asOfDate, resolvedLookback, cancellationToken);
        if (batch.Stocks.Count == 0)
        {
            throw new MarketDataUnavailableException(
                $"No live PSE history is available through {asOfDate:yyyy-MM-dd}, so the LLM benchmark cannot run.");
        }

        var barsByTicker = batch.Stocks.ToDictionary(
            stock => stock.Ticker,
            stock => stock.Bars.OrderBy(bar => bar.Date).ToList(),
            StringComparer.OrdinalIgnoreCase);

        var allDates = batch.Stocks
            .SelectMany(stock => stock.Bars)
            .Select(bar => bar.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        var minPrefixBars = Math.Max(QuantitativeFilterService.MinBarsRequired, profile.MinHistoryBars);
        var evaluableDates = new List<DateOnly>();

        if (allDates.Count > minPrefixBars + profile.ForwardWindowBars)
        {
            for (var index = minPrefixBars - 1; index + profile.ForwardWindowBars < allDates.Count; index++)
            {
                evaluableDates.Add(allDates[index]);
            }
        }

        if (evaluableDates.Count == 0)
        {
            throw new MarketDataUnavailableException(
                $"Not enough historical sessions ({allDates.Count}) to evaluate the {horizon} book with a " +
                $"{profile.ForwardWindowBars}-bar forward window.");
        }

        var sampledDates = SampleEvenly(evaluableDates, requestedSessions);
        var indexBars = (batch.IndexBars ?? Array.Empty<OhlcvBar>())
            .OrderBy(bar => bar.Date)
            .ToList();

        var llmAllItems = new List<SignalItem>();
        var quantAllItems = new List<SignalItem>();
        var sessionSummaries = new List<PromptEvalSessionDto>(sampledDates.Count);
        var rhos = new List<decimal>();
        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;
        var totalLatencyMs = 0;
        var modelName = string.Empty;

        foreach (var sessionDate in sampledDates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var indexPrefix = indexBars.Where(bar => bar.Date <= sessionDate).ToList();
            var (regime, regimeNote, pseiClose) = ResolveCausalRegime(indexPrefix, batch);

            var dayMetrics = new List<StockDailyMetric>();
            var barrierStatsByTicker = new Dictionary<string, BarrierStatistics>(StringComparer.OrdinalIgnoreCase);

            foreach (var stock in batch.Stocks)
            {
                var stockBars = barsByTicker[stock.Ticker];
                var prefixEnd = LastIndexOnOrBefore(stockBars, sessionDate);
                if (prefixEnd < minPrefixBars - 1)
                {
                    continue;
                }

                var prefixBars = stockBars.GetRange(0, prefixEnd + 1);
                var metric = _quantFilter.EvaluateStock(
                    stock with { Bars = prefixBars },
                    sessionDate,
                    indexPrefix,
                    computeEmpiricalBaseRate: true,
                    includeLiveForeignFlow: false);

                if (metric is null)
                {
                    continue;
                }

                dayMetrics.Add(metric);

                if (prefixBars.Count >= ProbabilisticEdgeService.MinimumHistoryBars)
                {
                    var stats = ProbabilisticEdgeService.ComputeBarrierStatistics(prefixBars, profile);
                    if (stats.Trials > 0)
                    {
                        barrierStatsByTicker[stock.Ticker] = stats;
                    }
                }
            }

            if (dayMetrics.Count == 0)
            {
                continue;
            }

            var scopedMetrics = dayMetrics
                .Select(metric => metric.WithDisclosures(MarketDataMapper.SerializeDisclosures(
                    QuantitativeFilterService.ScopeDisclosures(
                        MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson),
                        sessionDate,
                        profile.DisclosureLookbackDays))))
                .ToList();

            var candidates = _quantFilter.SelectCandidates(
                scopedMetrics,
                horizon,
                QuantitativeFilterService.MaxCandidates);

            if (candidates.Count == 0)
            {
                continue;
            }

            var breadth = QuantitativeFilterService.ComputeMarketBreadth(dayMetrics);
            var sectorRotation = SectorRotationService.Compute(dayMetrics);
            var playbooks = await RetrievePlaybooksSafeAsync(horizon, candidates, cancellationToken);

            var request = new OllamaSignalRequest(
                sessionDate,
                horizon,
                regime,
                regimeNote,
                pseiClose,
                NetForeignFlowPhp: null,
                batch.UsdPhp,
                candidates.Select(candidate => MarketDataMapper.ToCandidate(
                    candidate,
                    SectorRotationService.QuadrantFor(sectorRotation, candidate.Sector),
                    profile)).ToList(),
                YesterdayTop10: Array.Empty<PriorSignalInput>(),
                ReferencePlaybooks: playbooks,
                Breadth: breadth,
                SectorRotation: sectorRotation,
                DataQualityWarnings: new[]
                {
                    "Historical walk-forward evaluation: official DQR foreign flow is unreported."
                });

            var result = await _signalEngine.GenerateSignalAsync(request, cancellationToken);
            modelName = result.Metadata.Model;
            totalLatencyMs += result.Metadata.LatencyMs;
            totalPromptTokens += result.Metadata.PromptTokens ?? 0;
            totalCompletionTokens += result.Metadata.CompletionTokens ?? 0;

            var llmItems = BuildLlmItems(
                result.Output,
                candidates,
                profile,
                regime,
                breadth,
                sectorRotation,
                barrierStatsByTicker);

            var quantItems = BuildQuantBaselineItems(
                candidates,
                profile,
                regime,
                breadth,
                sectorRotation,
                barrierStatsByTicker);

            GradeCohort(llmItems, barsByTicker, sessionDate, profile.ForwardWindowBars);
            GradeCohort(quantItems, barsByTicker, sessionDate, profile.ForwardWindowBars);

            llmAllItems.AddRange(llmItems);
            quantAllItems.AddRange(quantItems);

            var rho = ComputeSpearmanRho(llmItems);
            if (rho is decimal measuredRho)
            {
                rhos.Add(measuredRho);
            }

            sessionSummaries.Add(new PromptEvalSessionDto(
                sessionDate,
                regime.ToString(),
                candidates.Count,
                llmItems.Count,
                quantItems.Count,
                rho,
                MeanRealizedROf(llmItems),
                MeanRealizedROf(quantItems),
                result.Metadata.LatencyMs,
                result.Metadata.PromptTokens,
                result.Metadata.CompletionTokens));
        }

        if (sessionSummaries.Count == 0)
        {
            throw new MarketDataUnavailableException(
                $"No sampled session between {sampledDates[0]:yyyy-MM-dd} and {sampledDates[^1]:yyyy-MM-dd} " +
                "produced eligible liquid candidates for evaluation.");
        }

        var llmSummary = SummarizeCohort(llmAllItems);
        var quantSummary = SummarizeCohort(quantAllItems);

        return new PromptEvalResultDto(
            horizon.ToString(),
            _signalEngine.EngineName,
            modelName,
            requestedSessions,
            sessionSummaries.Count,
            llmSummary,
            quantSummary,
            rhos.Count == 0 ? null : decimal.Round(rhos.Average(), 4, MidpointRounding.AwayFromZero),
            decimal.Round((llmSummary.WinRate - quantSummary.WinRate) * 100m, 2, MidpointRounding.AwayFromZero),
            decimal.Round(llmSummary.MeanRealizedNetR - quantSummary.MeanRealizedNetR, 4, MidpointRounding.AwayFromZero),
            decimal.Round(llmSummary.BrierScore - quantSummary.BrierScore, 4, MidpointRounding.AwayFromZero),
            totalPromptTokens,
            totalCompletionTokens,
            totalLatencyMs,
            sessionSummaries);
    }

    private List<SignalItem> BuildLlmItems(
        OllamaSignalOutput output,
        IReadOnlyList<StockDailyMetric> candidates,
        HorizonProfile profile,
        MarketRegime regime,
        MarketBreadth breadth,
        IReadOnlyList<SectorRotationDto> sectorRotation,
        IReadOnlyDictionary<string, BarrierStatistics> barrierStatsByTicker)
    {
        var candidateByTicker = candidates.ToDictionary(c => c.Ticker, StringComparer.OrdinalIgnoreCase);
        var quantRankByTicker = candidates
            .Select((candidate, index) => (candidate.Ticker, Rank: index + 1))
            .ToDictionary(entry => entry.Ticker, entry => entry.Rank, StringComparer.OrdinalIgnoreCase);

        var usedTickers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<SignalItem>(10);

        foreach (var pick in output.Top10 ?? new List<OllamaTopPick>())
        {
            if (items.Count >= 10)
            {
                break;
            }

            var ticker = (pick.Ticker ?? string.Empty).Trim().ToUpperInvariant();
            if (ticker.Length == 0 || !usedTickers.Add(ticker))
            {
                continue;
            }

            if (!candidateByTicker.TryGetValue(ticker, out var metric) || string.IsNullOrWhiteSpace(pick.Thesis))
            {
                continue;
            }

            var plan = profile.Horizon == SignalHorizon.DAY
                ? PseBoardLotService.NormalizeTradePlanWithinStaticLimits(
                    metric.ClosePrice,
                    pick.EntryPrice,
                    pick.StopLoss,
                    pick.TargetPrice)
                : PseBoardLotService.NormalizeTradePlan(pick.EntryPrice, pick.StopLoss, pick.TargetPrice);

            if (profile.RequireNetRewardRisk)
            {
                var netRr = PseBoardLotService.ComputeNetRiskReward(plan.Entry, plan.StopLoss, plan.Target);
                if (netRr < profile.MinimumNetRiskReward)
                {
                    continue;
                }
            }

            var item = ScoreItem(
                rank: items.Count + 1,
                quantRank: quantRankByTicker[ticker],
                convictionScore: (int)Math.Clamp(pick.ConvictionScore, 0m, 100m),
                plan,
                metric,
                profile,
                regime,
                breadth,
                sectorRotation,
                barrierStatsByTicker);
            items.Add(item);
        }

        return items;
    }

    private List<SignalItem> BuildQuantBaselineItems(
        IReadOnlyList<StockDailyMetric> candidates,
        HorizonProfile profile,
        MarketRegime regime,
        MarketBreadth breadth,
        IReadOnlyList<SectorRotationDto> sectorRotation,
        IReadOnlyDictionary<string, BarrierStatistics> barrierStatsByTicker)
    {
        var items = new List<SignalItem>(10);

        for (var index = 0; index < candidates.Count && items.Count < 10; index++)
        {
            var metric = candidates[index];
            if (metric.ClosePrice <= 0m || metric.Atr14 <= 0m)
            {
                continue;
            }

            var rawEntry = PseBoardLotService.RoundToValidTick(metric.ClosePrice);
            var rawStop = rawEntry - (profile.AtrStopMultiplier * metric.Atr14);
            var rawTarget = rawEntry + (profile.AtrTargetMultiplier * metric.Atr14);
            var plan = profile.Horizon == SignalHorizon.DAY
                ? PseBoardLotService.NormalizeTradePlanWithinStaticLimits(rawEntry, rawEntry, rawStop, rawTarget)
                : PseBoardLotService.NormalizeTradePlan(rawEntry, rawStop, rawTarget);

            if (plan.Entry <= 0m || plan.StopLoss <= 0m || plan.Target <= plan.Entry)
            {
                continue;
            }

            if (profile.RequireNetRewardRisk)
            {
                var netRr = PseBoardLotService.ComputeNetRiskReward(plan.Entry, plan.StopLoss, plan.Target);
                if (netRr < profile.MinimumNetRiskReward)
                {
                    continue;
                }
            }

            items.Add(ScoreItem(
                rank: items.Count + 1,
                quantRank: index + 1,
                convictionScore: metric.QuantPreScore,
                plan,
                metric,
                profile,
                regime,
                breadth,
                sectorRotation,
                barrierStatsByTicker));
        }

        return items;
    }

    private SignalItem ScoreItem(
        int rank,
        int quantRank,
        int convictionScore,
        (decimal Entry, decimal StopLoss, decimal Target) plan,
        StockDailyMetric metric,
        HorizonProfile profile,
        MarketRegime regime,
        MarketBreadth breadth,
        IReadOnlyList<SectorRotationDto> sectorRotation,
        IReadOnlyDictionary<string, BarrierStatistics> barrierStatsByTicker)
    {
        var effectiveFeeRate = PseBoardLotService.ComputeEffectiveRoundTripFeeRate(
            plan.Entry,
            SignalPipelineService.DefaultPositionBudgetPhp,
            metric.AvgTurnover20dPhp,
            metric.AtrPct);
        var tickSpreadPct = plan.Entry > 0m
            ? PseBoardLotService.ComputeTickSpreadRate(plan.Entry) * 100m
            : 0m;
        var barrierStats = barrierStatsByTicker.GetValueOrDefault(metric.Ticker);

        var assessment = _edge.EvaluateSetup(new SetupEdgeRequest(
            plan.Entry,
            plan.StopLoss,
            plan.Target,
            convictionScore,
            regime,
            profile,
            metric.EmpiricalWinRateFor(profile.Horizon),
            metric.RelativeStrength63d,
            metric.VolatilityContractionRatio,
            metric.WeinsteinStage,
            breadth.AboveSma50Pct,
            PseDisclosureClassifier.DetectTrapFlags(
                MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson)),
            metric.AtrPct,
            metric.TrailingDividendYieldPct,
            SectorRotationService.QuadrantFor(sectorRotation, metric.Sector),
            tickSpreadPct,
            metric.UpDownVolumeRatio20d,
            SlippageInclusiveFeeRate: effectiveFeeRate,
            Sector: metric.Sector,
            BarrierStats: barrierStats is { Trials: > 0 } ? barrierStats : null));

        return new SignalItem
        {
            Rank = rank,
            QuantRank = quantRank,
            QuantPreScore = metric.QuantPreScore,
            Ticker = metric.Ticker,
            ConvictionScore = convictionScore,
            EntryPrice = plan.Entry,
            StopLoss = plan.StopLoss,
            TargetPrice = plan.Target,
            RiskRewardRatio = PseBoardLotService.ComputeRiskReward(plan.Entry, plan.StopLoss, plan.Target),
            NetRiskRewardRatio = assessment.NetRiskReward,
            WinProbability = assessment.WinProbability,
            BreakEvenWinRate = assessment.BreakEvenWinRate,
            ExpectedValueR = assessment.ExpectedValueR,
            QuarterKellyFraction = assessment.QuarterKellyFraction,
            EdgeVerdict = assessment.Verdict
        };
    }

    private static void GradeCohort(
        IReadOnlyList<SignalItem> items,
        IReadOnlyDictionary<string, List<OhlcvBar>> barsByTicker,
        DateOnly runDate,
        int forwardWindowBars)
    {
        foreach (var item in items)
        {
            if (!barsByTicker.TryGetValue(item.Ticker, out var bars))
            {
                continue;
            }

            var forward = bars
                .Where(bar => bar.Date > runDate)
                .Take(forwardWindowBars)
                .Select(bar => new StockDailyMetric
                {
                    TradeDate = bar.Date,
                    Ticker = item.Ticker,
                    OpenPrice = bar.Open,
                    HighPrice = bar.High,
                    LowPrice = bar.Low,
                    ClosePrice = bar.Close
                })
                .ToList();

            SignalOutcomeService.ResolveItem(item, forward, forwardWindowBars);
        }
    }

    private static PromptEvalCohortSummaryDto SummarizeCohort(IReadOnlyList<SignalItem> items)
    {
        var filled = items
            .Where(item => item.OutcomeStatus != SignalOutcomeStatus.OPEN &&
                           item.OutcomeStatus != SignalOutcomeStatus.UNFILLED &&
                           item.RealizedNetR is decimal)
            .ToList();
        var unfilledCount = items.Count(item => item.OutcomeStatus == SignalOutcomeStatus.UNFILLED);
        var fillRate = items.Count == 0
            ? 0m
            : decimal.Round((decimal)filled.Count / items.Count, 4, MidpointRounding.AwayFromZero);

        var wins = filled.Count(item =>
            item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN);
        var winRate = filled.Count == 0
            ? 0m
            : decimal.Round((decimal)wins / filled.Count, 4, MidpointRounding.AwayFromZero);
        var meanR = filled.Count == 0
            ? 0m
            : decimal.Round(filled.Average(item => item.RealizedNetR!.Value), 4, MidpointRounding.AwayFromZero);

        var grossWin = filled.Where(item => item.RealizedNetR!.Value > 0m).Sum(item => item.RealizedNetR!.Value);
        var grossLoss = Math.Abs(filled.Where(item => item.RealizedNetR!.Value < 0m).Sum(item => item.RealizedNetR!.Value));
        var profitFactor = filled.Count == 0
            ? 0m
            : grossLoss == 0m
                ? (grossWin > 0m ? 99m : 0m)
                : decimal.Round(grossWin / grossLoss, 2, MidpointRounding.AwayFromZero);

        var forecasted = filled.Where(item => item.WinProbability > 0m).ToList();
        var brier = 0m;
        if (forecasted.Count > 0)
        {
            var sum = forecasted.Sum(item =>
            {
                var y = item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN
                    ? 1m
                    : 0m;
                var diff = item.WinProbability - y;
                return diff * diff;
            });
            brier = decimal.Round(sum / forecasted.Count, 4, MidpointRounding.AwayFromZero);
        }

        var top3 = filled.Where(item => item.Rank <= 3).ToList();
        var top3Wins = top3.Count(item =>
            item.OutcomeStatus is SignalOutcomeStatus.WIN_TARGET or SignalOutcomeStatus.EXPIRED_WIN);
        var top3WinRate = top3.Count == 0
            ? 0m
            : decimal.Round((decimal)top3Wins / top3.Count, 4, MidpointRounding.AwayFromZero);
        var top3MeanR = top3.Count == 0
            ? 0m
            : decimal.Round(top3.Average(item => item.RealizedNetR!.Value), 4, MidpointRounding.AwayFromZero);

        return new PromptEvalCohortSummaryDto(
            items.Count,
            filled.Count,
            unfilledCount,
            fillRate,
            winRate,
            meanR,
            profitFactor,
            brier,
            top3.Count,
            top3WinRate,
            top3MeanR);
    }

    private static decimal MeanRealizedROf(IReadOnlyList<SignalItem> items)
    {
        var values = items
            .Where(item => item.OutcomeStatus != SignalOutcomeStatus.OPEN &&
                           item.OutcomeStatus != SignalOutcomeStatus.UNFILLED &&
                           item.RealizedNetR is decimal)
            .Select(item => item.RealizedNetR!.Value)
            .ToList();

        return values.Count == 0
            ? 0m
            : decimal.Round(values.Average(), 4, MidpointRounding.AwayFromZero);
    }

    private static decimal? ComputeSpearmanRho(IReadOnlyList<SignalItem> items)
    {
        var measured = items.Where(item => item.QuantRank.HasValue).ToList();
        if (measured.Count < 3)
        {
            return null;
        }

        var quantOrder = measured
            .OrderBy(item => item.QuantRank!.Value)
            .ThenBy(item => item.Ticker, StringComparer.Ordinal)
            .Select((item, index) => (item.Ticker, Position: index + 1))
            .ToDictionary(entry => entry.Ticker, entry => entry.Position, StringComparer.OrdinalIgnoreCase);

        var sumDiffSq = measured.Sum(item =>
        {
            var diff = item.Rank - quantOrder[item.Ticker];
            return (decimal)(diff * diff);
        });

        var n = measured.Count;
        var rho = 1m - (6m * sumDiffSq) / (n * ((decimal)n * n - 1m));
        return decimal.Round(rho, 4, MidpointRounding.AwayFromZero);
    }

    private static List<DateOnly> SampleEvenly(IReadOnlyList<DateOnly> dates, int count)
    {
        if (dates.Count <= count)
        {
            return dates.ToList();
        }

        if (count == 1)
        {
            return new List<DateOnly> { dates[^1] };
        }

        var sampled = new List<DateOnly>(count);
        var lastIndex = -1;

        for (var i = 0; i < count; i++)
        {
            var index = (int)Math.Round(i * (dates.Count - 1d) / (count - 1d), MidpointRounding.AwayFromZero);
            if (index > lastIndex)
            {
                sampled.Add(dates[index]);
                lastIndex = index;
            }
        }

        return sampled;
    }

    private static int LastIndexOnOrBefore(IReadOnlyList<OhlcvBar> bars, DateOnly date)
    {
        var low = 0;
        var high = bars.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (bars[mid].Date <= date)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    private static (MarketRegime Regime, string Note, decimal Close) ResolveCausalRegime(
        IReadOnlyList<OhlcvBar> indexPrefix,
        MarketDataBatch fallbackBatch)
    {
        if (indexPrefix.Count == 0)
        {
            return (fallbackBatch.MarketRegime, fallbackBatch.RegimeNote, fallbackBatch.PseiClose);
        }

        var closes = indexPrefix.Select(bar => bar.Close).ToList();
        var close = closes[^1];
        var sma50 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaShortPeriod);
        var sma200 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaLongPeriod);

        if (sma50 is not decimal fast || sma200 is not decimal slow)
        {
            return (MarketRegime.NEUTRAL, "Insufficient PSEi history for 50D/200D averages; regime defaults to NEUTRAL.", close);
        }

        var regime = close >= fast && close >= slow
            ? MarketRegime.BULLISH
            : close < fast && close < slow
                ? MarketRegime.BEARISH
                : MarketRegime.NEUTRAL;

        return (regime, $"PSEi {close:0.##} vs 50D {fast:0.##} / 200D {slow:0.##}.", close);
    }

    private async Task<IReadOnlyList<KnowledgeSnippet>> RetrievePlaybooksSafeAsync(
        SignalHorizon horizon,
        IReadOnlyList<StockDailyMetric> candidates,
        CancellationToken cancellationToken)
    {
        if (_knowledge is null)
        {
            return Array.Empty<KnowledgeSnippet>();
        }

        try
        {
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

            return await _knowledge.RetrieveAsync(query, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Playbook retrieval failed during {Horizon} prompt evaluation.", horizon);
            return Array.Empty<KnowledgeSnippet>();
        }
    }
}
