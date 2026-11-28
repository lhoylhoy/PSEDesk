using System.Globalization;
using System.Text;
using PseSignalDesk.Application.Interfaces;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// On-demand single-ticker research for any PSE name — including tickers the daily catalog
/// does not track. It tries the live chart first, falls back to the latest stored snapshot
/// when the feed has nothing usable, and then runs the same deterministic engine the daily
/// run uses: horizon-matched ATR plan, empirical base rate for that horizon, fee-paid
/// probability, verdict and risk-sized order ticket. No synthetic candles are ever created.
/// </summary>
public sealed class TickerResearchService
{
    /// <summary>
    /// Notional the research page prices its simulated round-trip slippage at. It matches the
    /// pod-shop clip the trainer and the governor assume, so a researched name and a published
    /// pick are friction-priced on the same scale.
    /// </summary>
    public const decimal ResearchSizingNotionalPhp = 50_000m;

    private readonly IMarketDataProvider _marketData;
    private readonly ISignalRepository _signals;
    private readonly QuantitativeFilterService _quantFilter;
    private readonly ProbabilisticEdgeService _edge;
    private readonly PortfolioService _portfolio;
    private readonly SignalOutcomeService? _outcomes;

    public TickerResearchService(
        IMarketDataProvider marketData,
        ISignalRepository signals,
        QuantitativeFilterService quantFilter,
        ProbabilisticEdgeService edge,
        PortfolioService portfolio,
        SignalOutcomeService? outcomes = null)
    {
        _marketData = marketData;
        _signals = signals;
        _quantFilter = quantFilter;
        _edge = edge;
        _portfolio = portfolio;
        _outcomes = outcomes;
    }

    /// <summary>
    /// The tracked catalog, republished for the UI's company-name search. Read-only metadata:
    /// the desk still prices every name from the live feed.
    /// </summary>
    public IReadOnlyList<CatalogStockDto> GetCatalog() => _marketData.GetCatalog();

    public async Task<TickerResearchDto> ResearchAsync(
        string symbol,
        SignalHorizon horizon,
        CancellationToken cancellationToken = default,
        decimal? averageCost = null,
        int? shares = null)
    {
        var requested = (symbol ?? string.Empty).Trim();
        if (requested.Length == 0)
        {
            throw new MarketDataUnavailableException(
                "Provide a PSE ticker or company name, for example ICT, Jollibee or BDO.");
        }

        // Everyday readers know the company, not the symbol. A direct ticker always wins; an
        // unambiguous company-name match is resolved from the tracked catalog before any feed
        // call. An ambiguous query is never guessed at — it is passed through so the research
        // fails loudly instead of silently pricing the wrong company.
        var ticker = ResolveTicker(requested);

        var profile = HorizonProfile.For(horizon);
        var asOfDate = DateOnly.FromDateTime(DateTime.UtcNow);
        IReadOnlyList<OhlcvBar>? bars = null;
        StockDailyMetric? metric = null;
        var dataSource = "live";

        var live = await TryFetchLiveAsync(ticker, asOfDate, cancellationToken);
        IReadOnlyList<OhlcvBar> indexBars = Array.Empty<OhlcvBar>();

        if (live is not null && live.Bars.Count >= QuantitativeFilterService.MinBarsRequired)
        {
            bars = live.Bars;
            asOfDate = live.Bars[^1].Date;

            // Relative strength and beta are index-relative. The daily run passes the PSEi
            // bars into the same evaluator, so research prices them against the index too
            // instead of silently leaving those factors blank.
            indexBars = await TryFetchIndexBarsAsync(asOfDate, cancellationToken);
            metric = _quantFilter.EvaluateStock(live, asOfDate, indexBars);
        }

        if (metric is null)
        {
            dataSource = "stored";
            metric = await LoadStoredMetricAsync(ticker, cancellationToken);

            if (metric is not null)
            {
                // The research is only as fresh as the session it was computed from; echoing
                // today's date next to a stale row would overstate how current it is.
                asOfDate = metric.TradeDate;
            }
        }

        if (metric is null)
        {
            throw new MarketDataUnavailableException(
                $"{ticker} has no live chart with {QuantitativeFilterService.MinBarsRequired} sessions and no stored " +
                "session to research. Run the live pipeline once for a trading day that includes it, or check the ticker.");
        }

        var atr = metric.Atr14;
        var rawEntry = metric.ClosePrice;
        var rawStop = rawEntry - (profile.AtrStopMultiplier * atr);
        var rawTarget = rawEntry + (profile.AtrTargetMultiplier * atr);
        var plan = profile.Horizon == SignalHorizon.DAY
            ? PseBoardLotService.NormalizeTradePlanWithinStaticLimits(rawEntry, rawEntry, rawStop, rawTarget)
            : PseBoardLotService.NormalizeTradePlan(rawEntry, rawStop, rawTarget);

        // The base rate and payoff distribution must describe the barrier geometry of the horizon
        // being researched, so both are recomputed from the ticker's own series with that horizon's profile.
        var barrierStats = bars is not null
            ? ProbabilisticEdgeService.ComputeBarrierStatistics(bars, profile)
            : BarrierStatistics.None;
        var empiricalWinRate = barrierStats.Trials > 0
            ? barrierStats.WinRate
            : bars is not null
                ? null
                : metric.EmpiricalWinRate;

        var disclosures = MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson);
        var trapFlags = PseDisclosureClassifier.DetectTrapFlags(disclosures);

        // The sector dial is the last stored cross-section, so the research grades the name
        // against the same rotation read the desk is working from today. The same snapshot
        // carries the US 10-year yield used by the rate-gravity dial.
        var latestSnapshot = await TryLoadLatestSnapshotAsync(cancellationToken);
        var sectorRotation = latestSnapshot is null
            ? Array.Empty<SectorRotationDto>()
            : MarketDataMapper.DeserializeSectorRotation(latestSnapshot.SectorRotationJson);
        var tickSpreadPct = plan.Entry > 0m
            ? PseBoardLotService.ComputeTickSpreadRate(plan.Entry) * 100m
            : 0m;

        // Net foreign flow relative to the name's own 20-day turnover. Null whenever the
        // official Daily Quotation Report has not supplied a figure for this session.
        var foreignTurnoverRatio = metric.NetForeignTodayPhp is decimal netForeign &&
                                   metric.AvgTurnover20dPhp > 0m
            ? netForeign / metric.AvgTurnover20dPhp
            : (decimal?)null;

        // On-demand research must be priced by the same dials the daily run uses, or the same
        // name would show different odds depending on which door the reader came in through.
        // The regime is recomputed causally from the PSEi bars just fetched; the last stored
        // session is the only honest fallback when the index series is unavailable.
        var regime = ResolveRegime(indexBars, latestSnapshot?.MarketRegime);
        var feeRate = PseBoardLotService.ComputeEffectiveRoundTripFeeRate(
            plan.Entry,
            ResearchSizingNotionalPhp,
            metric.AvgTurnover20dPhp,
            metric.AtrPct);
        var learnedModel = await TryLoadLearnedModelAsync(horizon, cancellationToken);
        var calibrationOffsetPct = await TryLoadCalibrationOffsetPctAsync(horizon, cancellationToken);

        var assessment = _edge.EvaluateSetup(new SetupEdgeRequest(
            plan.Entry,
            plan.StopLoss,
            plan.Target,
            metric.QuantPreScore,
            regime,
            profile,
            empiricalWinRate,
            metric.RelativeStrength63d,
            metric.VolatilityContractionRatio,
            metric.WeinsteinStage,
            BreadthAboveSma50Pct: latestSnapshot?.BreadthAboveSma50Pct,
            TrapFlags: trapFlags,
            AtrPct: metric.AtrPct,
            TrailingDividendYieldPct: metric.TrailingDividendYieldPct,
            SectorQuadrant: SectorRotationService.QuadrantFor(sectorRotation, metric.Sector),
            TickSpreadPct: tickSpreadPct,
            UpDownVolumeRatio20d: metric.UpDownVolumeRatio20d,
            CalibrationOffsetPct: calibrationOffsetPct,
            SlippageInclusiveFeeRate: feeRate,
            NetForeignTurnoverRatio: foreignTurnoverRatio,
            Us10YClosePct: latestSnapshot?.Us10YClosePct,
            Sector: metric.Sector,
            LearnedModel: learnedModel,
            LearnedFeatures: TrainedModelFeatures.FromMetric(metric),
            BarrierStats: barrierStats.Trials > 0 ? barrierStats : null));

        OrderTicketDto? ticket = null;

        try
        {
            // The ticket is sized off the very assessment the page shows, so the peso figure and
            // the Kelly stake can never disagree.
            ticket = await _portfolio.GetOrderTicketAsync(
                ticker,
                plan.Entry,
                plan.StopLoss,
                plan.Target,
                assessment.Verdict,
                cancellationToken,
                assessment,
                horizon);
        }
        catch (PortfolioRuleViolationException)
        {
            // No configured bankroll yet: the research still stands, it just carries no size.
            ticket = null;
        }

        // The four-step roadmap is the same calculator the daily list uses, so a researched
        // name and a published pick can never print different cut-loss or chase levels.
        var wavePlan = WaveTradePlanCalculator.BuildPlan(
            plan.Entry,
            plan.StopLoss,
            plan.Target,
            metric.ClosePrice,
            metric.Ema20,
            assessment.Verdict,
            horizon);

        // An owner supplies their own average cost; the desk then answers the only question
        // that matters to them ("what do I do with this position?") instead of pricing a new
        // entry. Negative share counts are ignored rather than trusted.
        var heldShares = shares is int count && count > 0 ? count : 0;
        var positionAdvice = averageCost is decimal cost && cost > 0m
            ? WaveTradePlanCalculator.EvaluateHolding(
                ticker,
                cost,
                metric.ClosePrice,
                heldShares,
                atr,
                metric.Ema20,
                horizon)
            : null;

        return new TickerResearchDto(
            ticker,
            metric.CompanyName,
            metric.Sector,
            horizon.ToString(),
            asOfDate,
            InCatalog: _marketData.IsTracked(ticker),
            PassesLiquidityFloor: QuantitativeFilterService.MeetsLiquidityFloor(
                metric.AvgTurnover20dPhp,
                metric.MedianTurnover20dPhp,
                metric.ActiveTradingDays20d),
            metric.AvgTurnover20dPhp,
            MarketDataMapper.ToMetricsDto(metric),
            trapFlags.Select(flag => flag.ToString()).ToList(),
            MarketDataMapper.ToDisclosureDtos(metric.RecentDisclosuresJson),
            new ResearchPlanDto(
                plan.Entry,
                plan.StopLoss,
                plan.Target,
                PseBoardLotService.ComputeRiskReward(plan.Entry, plan.StopLoss, plan.Target),
                assessment.NetRiskReward,
                decimal.Round(profile.AtrStopMultiplier * atr, 4, MidpointRounding.AwayFromZero),
                decimal.Round(profile.AtrTargetMultiplier * atr, 4, MidpointRounding.AwayFromZero)),
            assessment.WinProbability,
            assessment.BreakEvenWinRate,
            assessment.ExpectedValueR,
            assessment.QuarterKellyFraction,
            assessment.Verdict.ToString(),
            $"A close below PHP {Format(plan.StopLoss)} invalidates the setup — exit there to protect your capital. " +
            $"Our historical estimate gives this plan about a {Format(assessment.WinProbability * 100m)}% chance of " +
            "hitting target after PSE fees.",
            ticket,
            dataSource,
            AiThesis: null,
            WavePlan: wavePlan,
            PositionAdvice: positionAdvice);
    }

    /// <summary>
    /// Resolves what the reader typed to a PSE symbol. A direct ticker always wins; otherwise an
    /// unambiguous company-name match in the tracked catalog is used ("Jollibee Foods
    /// Corporation" → JFC). Ambiguity is never guessed at: the raw text is passed through so
    /// the request fails loudly instead of pricing the wrong company.
    /// </summary>
    private string ResolveTicker(string requested)
    {
        var catalog = _marketData.GetCatalog();
        if (catalog.Count == 0)
        {
            return requested.ToUpperInvariant();
        }

        var direct = catalog.FirstOrDefault(
            stock => string.Equals(stock.Ticker, requested, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct.Ticker;
        }

        var query = NormalizeName(requested);
        if (query.Length == 0)
        {
            return requested.ToUpperInvariant();
        }

        var matches = catalog
            .Select(stock => (stock, name: NormalizeName(stock.CompanyName)))
            .Where(entry => entry.name.Length > 0 && entry.name.Contains(query, StringComparison.Ordinal))
            .Select(entry => entry.stock)
            .ToList();

        return matches.Count == 1 ? matches[0].Ticker : requested.ToUpperInvariant();
    }

    /// <summary>Lower-cased, punctuation-free words, so "SM Prime Holdings, Inc." reads "sm prime holdings inc".</summary>
    private static string NormalizeName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;

        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSpace = false;
            }
            else if (!lastWasSpace)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().Trim();
    }

    private async Task<StockMarketData?> TryFetchLiveAsync(
        string ticker,
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _marketData.FetchSingleStockAsync(ticker, asOfDate, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return null;
        }
        catch (MarketDataUnavailableException)
        {
            return null;
        }
    }

    private async Task<IReadOnlyList<OhlcvBar>> TryFetchIndexBarsAsync(
        DateOnly asOfDate,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _marketData.FetchIndexBarsAsync(asOfDate, cancellationToken);
        }
        catch (NotSupportedException)
        {
            return Array.Empty<OhlcvBar>();
        }
        catch (MarketDataUnavailableException)
        {
            return Array.Empty<OhlcvBar>();
        }
    }

    /// <summary>
    /// Last stored session, used for the sector cross-section and the macro dials. A research
    /// request for an untracked name still gets the sector read; when storage is empty the
    /// quadrant and the yield are simply unreported and the probability falls back to its
    /// other dials.
    /// </summary>
    private Task<DailyMarketSnapshot?> TryLoadLatestSnapshotAsync(CancellationToken cancellationToken) =>
        _signals.GetLatestSnapshotAsync(cancellationToken);

    private async Task<StockDailyMetric?> LoadStoredMetricAsync(string ticker, CancellationToken cancellationToken)
    {
        var snapshot = await _signals.GetLatestSnapshotAsync(cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        return await _signals.GetMetricAsync(snapshot.TradeDate, ticker, cancellationToken);
    }

    /// <summary>
    /// The index regime the research prices against. With a live PSEi series it is recomputed
    /// causally from that series (close versus its own 50- and 200-session averages); otherwise
    /// the stored session's regime is used, and only when neither exists does it fall back to
    /// NEUTRAL rather than inventing a read.
    /// </summary>
    private static MarketRegime ResolveRegime(
        IReadOnlyList<OhlcvBar> indexBars,
        MarketRegime? storedRegime)
    {
        var closes = indexBars.Select(bar => bar.Close).ToList();
        var sma50 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaShortPeriod);
        var sma200 = QuantitativeFilterService.ComputeSma(closes, QuantitativeFilterService.SmaLongPeriod);

        if (sma50 is not decimal shortAverage || sma200 is not decimal longAverage)
        {
            return storedRegime ?? MarketRegime.NEUTRAL;
        }

        var close = closes[^1];

        return close >= shortAverage && close >= longAverage
            ? MarketRegime.BULLISH
            : close < shortAverage && close < longAverage
                ? MarketRegime.BEARISH
                : MarketRegime.NEUTRAL;
    }

    /// <summary>
    /// The promoted money-manager model for this horizon, or null when none has earned
    /// promotion. A storage failure must not fail a research request, so it degrades to the
    /// structural prior.
    /// </summary>
    private async Task<LearnedEdgeModel?> TryLoadLearnedModelAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        try
        {
            return LearnedEdgeModel.FromActive(await _signals.GetTrainedModelAsync(horizon, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The desk's own walk-forward calibration offset for this horizon: how far the published
    /// probabilities have run above or below the outcomes they actually produced. Zero when the
    /// ledger has nothing graded yet.
    /// </summary>
    private async Task<decimal> TryLoadCalibrationOffsetPctAsync(
        SignalHorizon horizon,
        CancellationToken cancellationToken)
    {
        if (_outcomes is null)
        {
            return 0m;
        }

        try
        {
            return await _outcomes.ComputeLiveCalibrationOffsetPctAsync(horizon, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return 0m;
        }
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
