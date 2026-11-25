using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Pure, deterministic technical engine. It never calls an external service, which
/// makes the candidate list reproducible and unit-testable.
/// </summary>
public sealed class QuantitativeFilterService
{
    public const decimal LiquidityFloorPhp = 5_000_000m;
    public const int MaxCandidates = 25;
    public const int RsiPeriod = 14;
    public const int AtrPeriod = 14;
    public const int EmaPeriod = 20;
    public const int SmaShortPeriod = 50;
    public const int SmaLongPeriod = 200;
    public const int MinBarsRequired = SmaLongPeriod;
    public const int WeinsteinPeriod = 150;
    public const int WeinsteinSlopeLookback = 20;
    public const int Week52Period = 252;

    /// <summary>Window for the accumulation footprint (up/down volume ratio and money flow).</summary>
    public const int MoneyFlowPeriod = 20;

    /// <summary>Window for Kaufman's trend efficiency ratio.</summary>
    public const int TrendEfficiencyPeriod = 63;

    /// <summary>Window for empirical beta and the trailing maximum drawdown.</summary>
    public const int BetaPeriod = 252;

    /// <summary>
    /// Maximum pre-score achievable from verified technical pillars alone
    /// (trend 30 + RSI 20 + volume 15 + structure 20). Used to rescale the score
    /// when net foreign flow is unreported.
    /// </summary>
    public const decimal TechnicalScoreMaximum = 85m;

    /// <summary>
    /// Net foreign flow as a share of 20-day turnover that counts as a confirmed institutional
    /// footprint (±5%). Below the band the print is treated as noise, not as a tilt.
    /// </summary>
    public const decimal ForeignFlowStrongRatio = 0.05m;

    /// <summary>
    /// Computes one metric row per stock that has enough history. Stocks with fewer
    /// than 200 candles are skipped because SMA200 would be meaningless.
    /// </summary>
    public IReadOnlyList<StockDailyMetric> EvaluateBatch(MarketDataBatch batch)
    {
        var results = new List<StockDailyMetric>(batch.Stocks.Count);

        foreach (var stock in batch.Stocks)
        {
            var metric = EvaluateStock(stock, batch.TradeDate, batch.IndexBars);
            if (metric is not null)
            {
                results.Add(metric);
            }
        }

        return results
            .OrderByDescending(m => m.QuantPreScore)
            .ThenByDescending(m => m.AvgTurnover20dPhp)
            .ThenBy(m => m.Ticker, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Builds the complete technical picture for a single stock, or null when there
    /// is not enough OHLCV history to compute the 200-day trend. <paramref name="indexBars"/>
    /// is optional so the historical callers and unit tests keep working; when supplied it
    /// produces the 63-session relative strength versus the PSEi.
    /// <paramref name="computeEmpiricalBaseRate"/> lets the walk-forward backtester skip the
    /// O(n²) barrier study and supply its own causal, cached base rate instead.
    /// <paramref name="includeLiveForeignFlow"/> must be <c>false</c> on every historical replay:
    /// <see cref="StockMarketData.NetForeignTodayPhp"/> is the <b>live</b> session's official flow,
    /// so feeding it into a 2023 prefix hands the scorer today's foreign print and leaks the
    /// future into both the training labels and the backtest curve.
    /// </summary>
    public StockDailyMetric? EvaluateStock(
        StockMarketData stock,
        DateOnly tradeDate,
        IReadOnlyList<OhlcvBar>? indexBars = null,
        bool computeEmpiricalBaseRate = true,
        bool includeLiveForeignFlow = true)
    {
        var bars = stock.Bars.OrderBy(b => b.Date).ToList();
        if (bars.Count < MinBarsRequired)
        {
            return null;
        }

        var last = bars[^1];
        if (last.Close <= 0m)
        {
            return null;
        }

        // The live foreign print belongs to today's session only. A replay passes false and gets
        // the same "unreported" treatment a session without a DQR upload gets.
        var liveForeignFlow = includeLiveForeignFlow ? stock.NetForeignTodayPhp : null;

        var closes = bars.Select(b => b.Close).ToList();

        // Every downstream invariant (trend alignment, RSI band, ATR-based structure)
        // needs a real indicator. Missing inputs mean the series is unusable, so the
        // stock is skipped instead of being scored on invented defaults.
        var rsi = ComputeRsi(closes, RsiPeriod);
        var ema20 = ComputeEma(closes, EmaPeriod);
        var sma50 = ComputeSma(closes, SmaShortPeriod);
        var sma200 = ComputeSma(closes, SmaLongPeriod);
        var atr = ComputeAtr(bars, AtrPeriod);

        if (rsi is null || ema20 is null || sma50 is null || sma200 is null || atr is null)
        {
            return null;
        }

        var sma150 = ComputeSma(closes, WeinsteinPeriod);
        var week52High = ComputeWeek52High(closes);
        var weinsteinStage = ComputeWeinsteinStage(closes);

        // New probabilistic inputs. Each is nullable so an old stored session (or a short
        // history) degrades to "unreported" instead of a fabricated value.
        var atrPct = atr.Value / last.Close * 100m;
        var volatilityContraction = ComputeVolatilityContractionRatio(bars);
        var relativeStrength = ComputeRelativeStrength63d(bars, indexBars);

        // Each horizon is graded against its own barrier geometry and forward window. Before
        // this, one WEEK-shaped base rate priced all four books, which quietly mis-stated DAY
        // and YEAR odds. One sweep of the history yields all four rates.
        var empiricalWinRates = computeEmpiricalBaseRate
            ? ProbabilisticEdgeService.ComputeHorizonBaseRates(bars)
            : null;

        var empiricalWinRateWeek = empiricalWinRates?[SignalHorizon.WEEK];
        var empiricalWinRateDay = empiricalWinRates?[SignalHorizon.DAY];
        var empiricalWinRateMonth = empiricalWinRates?[SignalHorizon.MONTH];
        var empiricalWinRateYear = empiricalWinRates?[SignalHorizon.YEAR];

        // Institutional footprints: accumulation (up/down volume, money flow), trend quality
        // (efficiency ratio) and risk quality (beta, worst drawdown).
        var upDownVolumeRatio = ComputeUpDownVolumeRatio(bars);
        var moneyFlow = ComputeChaikinMoneyFlow(bars);
        var trendEfficiency = ComputeTrendEfficiency(bars);
        var beta = ComputeBeta(bars, indexBars);
        var maxDrawdown = ComputeMaxDrawdownPct(closes);

        // Income inputs. A MONTH/YEAR mandate needs the real cash yield, and an ex-date
        // inside the last three sessions must be labelled so a mechanical gap is not read
        // as a breakdown.
        var dividends = ComputeTrailingDividends(stock.Dividends, last.Close, tradeDate);
        var disclosures = BuildDisclosures(
            ScopeToSession(stock.Disclosures, tradeDate),
            dividends.LastExDate,
            dividends.TrailingPerShare,
            tradeDate);

        var rsiValue = rsi.Value;
        var ema20Value = ema20.Value;
        var sma50Value = sma50.Value;
        var sma200Value = sma200.Value;
        var atrValue = atr.Value;

        var window = bars.TakeLast(20).ToList();
        var avgVolume = window.Average(b => (double)b.Volume);
        var avgTurnover = window.Average(b => b.Close * b.Volume);
        var volumeRatio = avgVolume <= 0d ? 0m : (decimal)(last.Volume / avgVolume);

        // Robust liquidity: the mean turnover of twenty sessions is one block trade away from
        // passing a floor that most sessions never reach. The median and the count of sessions
        // that actually printed volume are the numbers a desk can rely on.
        var medianTurnover = ComputeMedianTurnover20d(window);
        var activeTradingDays = window.Count(bar => bar.Volume > 0);

        var support = PseBoardLotService.RoundToValidTick(window.Min(b => b.Low));
        var resistance = PseBoardLotService.RoundToValidTick(window.Max(b => b.High));

        var previousClose = bars[^2].Close;
        var changePct = previousClose <= 0m
            ? 0m
            : decimal.Round((last.Close - previousClose) / previousClose * 100m, 2, MidpointRounding.AwayFromZero);

        var score = ComputePreScore(
            last.Close,
            ema20Value,
            sma50Value,
            sma200Value,
            rsiValue,
            volumeRatio,
            avgTurnover,
            liveForeignFlow,
            atrValue,
            support,
            resistance);

        return new StockDailyMetric
        {
            TradeDate = tradeDate,
            Ticker = stock.Ticker,
            CompanyName = stock.CompanyName,
            Sector = stock.Sector,
            ClosePrice = last.Close,
            ChangePct = changePct,
            AvgTurnover20dPhp = decimal.Round(avgTurnover, 2, MidpointRounding.AwayFromZero),
            MedianTurnover20dPhp = medianTurnover,
            ActiveTradingDays20d = activeTradingDays,
            VolumeRatioVs20d = decimal.Round(volumeRatio, 2, MidpointRounding.AwayFromZero),
            Rsi14 = decimal.Round(rsiValue, 2, MidpointRounding.AwayFromZero),
            Ema20 = decimal.Round(ema20Value, 4, MidpointRounding.AwayFromZero),
            Sma50 = decimal.Round(sma50Value, 4, MidpointRounding.AwayFromZero),
            Sma200 = decimal.Round(sma200Value, 4, MidpointRounding.AwayFromZero),
            Atr14 = decimal.Round(atrValue, 4, MidpointRounding.AwayFromZero),
            SupportLevel = support,
            ResistanceLevel = resistance,
            NetForeignTodayPhp = liveForeignFlow is decimal netForeign
                ? decimal.Round(netForeign, 2, MidpointRounding.AwayFromZero)
                : null,
            RecentDisclosuresJson = MarketDataMapper.SerializeDisclosures(disclosures),
            QuantPreScore = score,
            Sma150 = sma150 is decimal s150 ? decimal.Round(s150, 4, MidpointRounding.AwayFromZero) : null,
            Week52High = week52High is decimal high ? PseBoardLotService.RoundToValidTick(high) : null,
            WeinsteinStage = weinsteinStage,
            OpenPrice = last.Open,
            HighPrice = last.High,
            LowPrice = last.Low,
            AtrPct = decimal.Round(atrPct, 2, MidpointRounding.AwayFromZero),
            VolatilityContractionRatio = volatilityContraction,
            RelativeStrength63d = relativeStrength,
            EmpiricalWinRate = empiricalWinRateWeek,
            EmpiricalWinRateDay = empiricalWinRateDay,
            EmpiricalWinRateMonth = empiricalWinRateMonth,
            EmpiricalWinRateYear = empiricalWinRateYear,
            UpDownVolumeRatio20d = upDownVolumeRatio,
            ChaikinMoneyFlow20d = moneyFlow,
            TrendEfficiency63d = trendEfficiency,
            Beta252d = beta,
            MaxDrawdown252dPct = maxDrawdown,
            TrailingDividendPerShare = dividends.TrailingPerShare,
            TrailingDividendYieldPct = dividends.YieldPct,
            LastExDividendDate = dividends.LastExDate
        };
    }

    /// <summary>
    /// Sums the cash dividends whose ex-date falls inside the trailing 365 calendar days
    /// and expresses them as a yield on the close. A feed with no dividend in the window
    /// returns nulls (unreported) rather than a zero, because "paid nothing this year" and
    /// "we did not see a payment" are different claims.
    /// </summary>
    public static (decimal? TrailingPerShare, decimal? YieldPct, DateOnly? LastExDate) ComputeTrailingDividends(
        IReadOnlyList<StockDividendEvent>? dividends,
        decimal closePrice,
        DateOnly tradeDate)
    {
        if (dividends is null || dividends.Count == 0)
        {
            return (null, null, null);
        }

        var windowStart = tradeDate.AddDays(-365);

        var inWindow = dividends
            .Where(dividend => dividend.AmountPerShare > 0m &&
                               dividend.ExDate <= tradeDate &&
                               dividend.ExDate >= windowStart)
            .OrderBy(dividend => dividend.ExDate)
            .ToList();

        if (inWindow.Count == 0)
        {
            return (null, null, null);
        }

        var total = inWindow.Sum(dividend => dividend.AmountPerShare);
        var lastExDate = inWindow[^1].ExDate;
        decimal? yieldPct = closePrice > 0m
            ? decimal.Round(total / closePrice * 100m, 2, MidpointRounding.AwayFromZero)
            : null;

        return (
            decimal.Round(total, 4, MidpointRounding.AwayFromZero),
            yieldPct,
            lastExDate);
    }

    /// <summary>
    /// Adds a synthetic ex-dividend disclosure when the last ex-date landed inside the
    /// last three calendar days, so the deterministic classifier raises
    /// <c>EX_DIVIDEND_DROP</c> and neither the model nor the UI calls the markdown a
    /// technical breakdown.
    /// </summary>
    private static IReadOnlyList<StockDisclosure> BuildDisclosures(
        IReadOnlyList<StockDisclosure>? raw,
        DateOnly? lastExDate,
        decimal? trailingPerShare,
        DateOnly tradeDate)
    {
        var disclosures = raw is null
            ? new List<StockDisclosure>()
            : raw.ToList();

        if (lastExDate is not DateOnly exDate || exDate < tradeDate.AddDays(-3) || exDate > tradeDate)
        {
            return disclosures;
        }

        var amount = trailingPerShare is decimal perShare ? $"{perShare:0.####}" : "cash";
        var headline = $"Cash dividend PHP {amount} per share went ex-dividend on {exDate:yyyy-MM-dd}";

        if (!disclosures.Any(existing =>
                existing.Headline.Contains("ex-dividend", StringComparison.OrdinalIgnoreCase) &&
                existing.Date == exDate))
        {
            disclosures.Add(new StockDisclosure(exDate, headline, "Dividend"));
        }

        return disclosures;
    }

    /// <summary>
    /// Applies the hard liquidity floor and returns the highest-scoring candidates.
    /// </summary>
    public IReadOnlyList<StockDailyMetric> SelectCandidates(
        IEnumerable<StockDailyMetric> metrics,
        int maxCandidates = MaxCandidates)
    {
        return metrics
            .Where(m => MeetsLiquidityFloor(m.AvgTurnover20dPhp, m.MedianTurnover20dPhp, m.ActiveTradingDays20d))
            .OrderByDescending(m => m.QuantPreScore)
            .ThenByDescending(m => m.AvgTurnover20dPhp)
            .ThenBy(m => m.Ticker, StringComparer.Ordinal)
            .Take(maxCandidates)
            .ToList();
    }

    /// <summary>
    /// Horizon-aware candidate selection. On top of the liquidity floor it hard-rejects
    /// names carrying a structural trap that makes a long untradeable (rights offering,
    /// tender offer, trading halt) and orders the rest by a horizon-adjusted score, so an
    /// intraday book prefers fresh volume expansion while a yearly book prefers trend and
    /// proximity to the 52-week high.
    /// </summary>
    public IReadOnlyList<StockDailyMetric> SelectCandidates(
        IEnumerable<StockDailyMetric> metrics,
        SignalHorizon horizon,
        int maxCandidates)
    {
        var profile = HorizonProfile.For(horizon);
        var eligible = new List<(StockDailyMetric Metric, decimal Score)>();

        foreach (var metric in metrics)
        {
            if (!MeetsLiquidityFloor(metric.AvgTurnover20dPhp, metric.MedianTurnover20dPhp, metric.ActiveTradingDays20d))
            {
                continue;
            }

            var disclosures = MarketDataMapper.DeserializeDisclosures(metric.RecentDisclosuresJson);
            var trapFlags = PseDisclosureClassifier.DetectTrapFlags(disclosures);
            if (PseDisclosureClassifier.HasHardDisqualifier(trapFlags))
            {
                continue;
            }

            eligible.Add((metric, ComputeHorizonScore(metric, profile)));
        }

        return eligible
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Metric.AvgTurnover20dPhp)
            .ThenBy(entry => entry.Metric.Ticker, StringComparer.Ordinal)
            .Take(maxCandidates)
            .Select(entry => entry.Metric)
            .ToList();
    }

    /// <summary>
    /// Deterministic horizon adjustment on top of <see cref="StockDailyMetric.QuantPreScore"/>.
    /// Each book runs its own sleeve of evidence: DAY pays for accumulation and punishes a
    /// range too tight to clear the round trip, WEEK wants a confirmed Stage 2 with volume
    /// expansion, MONTH wants trend quality plus cash yield, and YEAR wants compounding
    /// quality (smooth path, survivable drawdown, defensive beta). Never below zero and never
    /// above 100 so the prompt and the UI stay on one scale.
    /// </summary>
    public static decimal ComputeHorizonScore(StockDailyMetric metric, HorizonProfile profile)
    {
        var score = (decimal)metric.QuantPreScore;
        score += ScoreRsiSleeve(metric, profile);

        score += profile.Horizon switch
        {
            SignalHorizon.DAY => ScoreDaySleeve(metric),
            SignalHorizon.WEEK => ScoreWeekSleeve(metric),
            SignalHorizon.MONTH => ScoreMonthSleeve(metric),
            _ => ScoreYearSleeve(metric)
        };

        return Math.Clamp(score + ScoreSharedSleeve(metric), 0m, 100m);
    }

    private static decimal ScoreRsiSleeve(StockDailyMetric metric, HorizonProfile profile)
    {
        if (metric.Rsi14 >= profile.RsiIdealLow && metric.Rsi14 <= profile.RsiIdealHigh)
        {
            return 8m;
        }

        if (metric.Rsi14 > profile.RsiIdealHigh + 5m)
        {
            // Chasing an extended print is the classic intraday loss; both DAY and WEEK
            // profiles punish it, and MONTH/YEAR simply prefer a cooler tape.
            return -8m;
        }

        return metric.Rsi14 < 30m ? -4m : 0m;
    }

    /// <summary>Volume expansion plus Weinstein stage, the participation test.</summary>
    private static decimal ScoreParticipation(StockDailyMetric metric)
    {
        var score = metric.VolumeRatioVs20d >= 1.5m ? 5m : 0m;

        return score + metric.WeinsteinStage switch
        {
            "STAGE_2" => 4m,
            "STAGE_3" => -6m,
            "STAGE_4" => -15m,
            _ => 0m
        };
    }

    /// <summary>
    /// Up/down volume ratio and Chaikin money flow — the closest public proxy for the
    /// institutional accumulation a net-foreign-flow column would show.
    /// </summary>
    private static decimal ScoreAccumulationFootprint(StockDailyMetric metric, decimal strongHit, decimal flowHit)
    {
        var score = 0m;

        if (metric.UpDownVolumeRatio20d is decimal ratio)
        {
            score += ratio >= 1.25m ? strongHit : ratio < 0.70m ? -strongHit : 0m;
        }

        if (metric.ChaikinMoneyFlow20d is decimal flow)
        {
            // CMF is a ratio in [-1, 1]; ±0.10 is a meaningful one-sided footprint.
            score += flow >= 0.10m ? flowHit : flow <= -0.10m ? -flowHit : 0m;
        }

        return score;
    }

    /// <summary>Kaufman efficiency ratio: a smooth institutional path pays; chop does not.</summary>
    private static decimal ScoreTrendEfficiency(StockDailyMetric metric, decimal smoothHit, decimal chopPenalty)
    {
        if (metric.TrendEfficiency63d is not decimal efficiency)
        {
            return 0m;
        }

        if (efficiency >= 0.30m)
        {
            return smoothHit;
        }

        return efficiency < 0.15m ? -chopPenalty : 0m;
    }

    private static decimal ScoreDaySleeve(StockDailyMetric metric)
    {
        var score = ScoreParticipation(metric);

        // An intraday move must clear the round trip (0.69% fee-only, before the spread). When ATR14
        // is under 1.5% of price the session's whole range is roughly two round trips wide, so
        // there is no honest intraday edge left after friction.
        if (metric.AtrPct is decimal atrPct && atrPct > 0m && atrPct < 1.5m)
        {
            score -= 6m;
        }

        return score + ScoreAccumulationFootprint(metric, strongHit: 3m, flowHit: 2m);
    }

    private static decimal ScoreWeekSleeve(StockDailyMetric metric)
    {
        return ScoreParticipation(metric)
            + ScoreAccumulationFootprint(metric, strongHit: 4m, flowHit: 3m)
            + ScoreTrendEfficiency(metric, smoothHit: 3m, chopPenalty: 4m);
    }

    private static decimal ScoreMonthSleeve(StockDailyMetric metric)
    {
        return ScoreStructuralTrendSleeve(metric)
            + ScoreTrendEfficiency(metric, smoothHit: 4m, chopPenalty: 4m)
            + ScoreDrawdownQuality(metric, qualityHit: 3m, impairmentPenalty: 3m)
            + ScoreDefensiveBeta(metric, defensiveHit: 2m, aggressivePenalty: 0m)
            + ScoreAccumulationFootprint(metric, strongHit: 0m, flowHit: 2m);
    }

    private static decimal ScoreYearSleeve(StockDailyMetric metric)
    {
        return ScoreStructuralTrendSleeve(metric)
            + ScoreTrendEfficiency(metric, smoothHit: 5m, chopPenalty: 5m)
            + ScoreDrawdownQuality(metric, qualityHit: 4m, impairmentPenalty: 5m)
            + ScoreDefensiveBeta(metric, defensiveHit: 3m, aggressivePenalty: 3m)
            + (metric.WeinsteinStage == "STAGE_2" ? 4m : 0m);
    }

    /// <summary>
    /// The MONTH/YEAR core: long-term trend alignment, cash yield as part of total return and
    /// Minervini's proximity rule (leaders sit close to their highs).
    /// </summary>
    private static decimal ScoreStructuralTrendSleeve(StockDailyMetric metric)
    {
        var score = metric.Sma50 > metric.Sma200 ? 6m : 0m;

        // Cash yield is part of total return. A 4.5%+ payer above its 200-day average is a
        // compounding position, not a value trap.
        if (metric.TrailingDividendYieldPct is decimal dividendYield &&
            dividendYield >= 4.5m &&
            metric.Sma200 > 0m &&
            metric.ClosePrice > metric.Sma200)
        {
            score += 4m;
        }

        if (metric.Week52High is decimal high && high > 0m)
        {
            var drawdown = (high - metric.ClosePrice) / high;

            // A name 35% under its 52-week high is a repair story, not a leader.
            if (drawdown <= 0.10m)
            {
                score += 6m;
            }
            else if (drawdown >= 0.35m)
            {
                score -= 4m;
            }
        }

        return score;
    }

    /// <summary>
    /// Worst trailing-year drawdown. A long book wants a name whose holder already survived
    /// its own history; a 50% hole means the risk was mispriced in the past.
    /// </summary>
    private static decimal ScoreDrawdownQuality(StockDailyMetric metric, decimal qualityHit, decimal impairmentPenalty)
    {
        if (metric.MaxDrawdown252dPct is not decimal worst)
        {
            return 0m;
        }

        if (worst <= 25m)
        {
            return qualityHit;
        }

        return worst >= 50m ? -impairmentPenalty : 0m;
    }

    /// <summary>
    /// Empirical beta against the PSEi over the trailing year. Low beta is how a pod keeps
    /// its correlation budget for conviction instead of paying it to the index.
    /// </summary>
    private static decimal ScoreDefensiveBeta(StockDailyMetric metric, decimal defensiveHit, decimal aggressivePenalty)
    {
        if (metric.Beta252d is not decimal beta)
        {
            return 0m;
        }

        if (beta <= 1.15m)
        {
            return defensiveHit;
        }

        return beta >= 1.60m && aggressivePenalty > 0m ? -aggressivePenalty : 0m;
    }

    /// <summary>
    /// Evidence that applies to every book: relative strength versus the PSEi, volatility
    /// contraction and enough turnover to absorb an institutional clip.
    /// </summary>
    private static decimal ScoreSharedSleeve(StockDailyMetric metric)
    {
        var score = 0m;

        // Leaders outperforming the index out of a tight base have a higher hit rate than
        // laggards in wide ranges.
        if (metric.RelativeStrength63d is decimal relativeStrength)
        {
            if (relativeStrength >= 2m)
            {
                score += 4m;
            }
            else if (relativeStrength <= -2m)
            {
                score -= 4m;
            }
        }

        if (metric.VolatilityContractionRatio is decimal contraction && contraction < 0.85m)
        {
            score += 4m;
        }

        if (metric.AvgTurnover20dPhp >= 50_000_000m)
        {
            score += 2m;
        }

        // Confirmed foreign demand, once the official PSE Daily Quotation Report supplies it.
        // The ratio (net foreign / 20-day turnover) is scale-free, so a PHP 200M net buy in a
        // PHP 2B name is not treated like the same print in a PHP 200M name. Unreported flow
        // contributes nothing rather than being read as a neutral zero.
        if (metric.NetForeignTodayPhp is decimal netForeign && metric.AvgTurnover20dPhp > 0m)
        {
            var foreignRatio = netForeign / metric.AvgTurnover20dPhp;

            if (foreignRatio >= ForeignFlowStrongRatio)
            {
                score += 3m;
            }
            else if (foreignRatio <= -ForeignFlowStrongRatio)
            {
                score -= 4m;
            }
        }

        return score;
    }

    /// <summary>
    /// Merge window for the rolling disclosure view: a rights offering or tender offer filed
    /// three weeks ago must still disqualify a name today, not silently disappear because the
    /// Edge page rolled over.
    /// </summary>
    public const int MaxRollingDisclosureLookbackDays = 120;

    /// <summary>Cap on the merged disclosure history kept per ticker.</summary>
    public const int MaxRollingDisclosures = 6;

    /// <summary>
    /// Merges the current session's filings with the names' recent history and keeps the most
    /// relevant <see cref="MaxRollingDisclosures"/> items inside the window. Structural trap
    /// headlines outrank plain announcements, so a hard disqualifier filed last month survives
    /// a flood of routine 17-C releases; within the same priority the newest filing wins.
    /// </summary>
    public static IReadOnlyList<StockDisclosure> MergeRollingDisclosures(
        IReadOnlyList<StockDisclosure>? current,
        IReadOnlyList<StockDisclosure>? historical,
        DateOnly tradeDate,
        int lookbackDays = MaxRollingDisclosureLookbackDays)
    {
        var windowStart = tradeDate.AddDays(-Math.Abs(lookbackDays));
        var merged = new List<StockDisclosure>();
        var seen = new HashSet<(DateOnly Date, string Headline)>();

        foreach (var disclosure in (current ?? Array.Empty<StockDisclosure>())
                     .Concat(historical ?? Array.Empty<StockDisclosure>()))
        {
            if (disclosure is null ||
                disclosure.Date < windowStart ||
                disclosure.Date > tradeDate ||
                string.IsNullOrWhiteSpace(disclosure.Headline))
            {
                continue;
            }

            var key = (disclosure.Date, disclosure.Headline.Trim().ToUpperInvariant());
            if (seen.Add(key))
            {
                merged.Add(disclosure);
            }
        }

        return merged
            .OrderByDescending(disclosure => TrapPriority(disclosure.Headline))
            .ThenByDescending(disclosure => disclosure.Date)
            .Take(MaxRollingDisclosures)
            .OrderByDescending(disclosure => disclosure.Date)
            .ThenByDescending(disclosure => TrapPriority(disclosure.Headline))
            .ToList();
    }

    /// <summary>
    /// Narrows a disclosure list to one horizon's lookback window without merging or
    /// re-ranking. The pipeline persists the full rolling history but only shows a DAY book the
    /// last week of filings. Filings dated after the session — or dated the same day but
    /// timestamped after the close — are dropped, so a replay cannot act on a document that had
    /// not been published yet.
    /// </summary>
    public static IReadOnlyList<StockDisclosure> ScopeDisclosures(
        IReadOnlyList<StockDisclosure>? disclosures,
        DateOnly tradeDate,
        int lookbackDays,
        TimeOnly? cutoff = null)
    {
        if (disclosures is null || disclosures.Count == 0)
        {
            return Array.Empty<StockDisclosure>();
        }

        var windowStart = tradeDate.AddDays(-Math.Abs(lookbackDays));

        return disclosures
            .Where(disclosure => disclosure.Date >= windowStart && IsAvailableBy(disclosure, tradeDate, cutoff))
            .OrderByDescending(disclosure => disclosure.Date)
            .ToList();
    }

    private static int TrapPriority(string? headline)
    {
        var classification = PseDisclosureClassifier.Classify(headline);

        if (PseDisclosureClassifier.IsHardDisqualifier(classification.TrapFlag))
        {
            return 2;
        }

        return classification.TrapFlag == PseTrapFlag.NONE ? 0 : 1;
    }

    public static bool MeetsLiquidityFloor(decimal avgTurnover20dPhp) => avgTurnover20dPhp >= LiquidityFloorPhp;

    /// <summary>A 20-session run of trading days the desk requires before it will call a name liquid.</summary>
    public const int MinimumActiveTradingDays20d = 16;

    /// <summary>
    /// The robust liquidity gate: the mean turnover, the <b>median</b> turnover and the number of
    /// sessions that actually printed volume all have to clear their floors. A single block trade
    /// can lift the mean of twenty quiet sessions over <c>PHP 5M</c> while the median stays at a
    /// fraction of it, and a name that did not trade on six of twenty sessions cannot be exited
    /// in one clip. A session stored before the robust columns existed reports zero for both, and
    /// falls back to the mean so history is never rejected by a column that did not exist yet.
    /// </summary>
    public static bool MeetsLiquidityFloor(
        decimal avgTurnover20dPhp,
        decimal medianTurnover20dPhp = 0m,
        int activeTradingDays20d = 0)
    {
        if (avgTurnover20dPhp < LiquidityFloorPhp)
        {
            return false;
        }

        if (medianTurnover20dPhp <= 0m || activeTradingDays20d <= 0)
        {
            return true;
        }

        return medianTurnover20dPhp >= LiquidityFloorPhp && activeTradingDays20d >= MinimumActiveTradingDays20d;
    }

    /// <summary>Median close-times-volume of the supplied window, rounded to the centavo.</summary>
    public static decimal ComputeMedianTurnover20d(IReadOnlyList<OhlcvBar> window)
    {
        if (window is null || window.Count == 0)
        {
            return 0m;
        }

        var turnovers = window
            .Select(bar => bar.Close * bar.Volume)
            .OrderBy(value => value)
            .ToList();

        var middle = turnovers.Count / 2;
        var median = turnovers.Count % 2 == 0
            ? (turnovers[middle - 1] + turnovers[middle]) / 2m
            : turnovers[middle];

        return decimal.Round(median, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Drops every filing the desk could not have read before the session closed. A filing dated
    /// after <paramref name="tradeDate"/> is future information outright; a filing dated the same
    /// day is only usable when its timestamp precedes the cutoff (the 15:00 close by default).
    /// Without this a 2023 replay could disqualify a trade because of a 2025 rights offering.
    /// </summary>
    public static IReadOnlyList<StockDisclosure> ScopeToSession(
        IReadOnlyList<StockDisclosure>? disclosures,
        DateOnly tradeDate,
        TimeOnly? cutoff = null)
    {
        if (disclosures is null || disclosures.Count == 0)
        {
            return Array.Empty<StockDisclosure>();
        }

        return disclosures
            .Where(disclosure => IsAvailableBy(disclosure, tradeDate, cutoff))
            .ToList();
    }

    /// <summary>
    /// True when a filing was on the wire by the cutoff of <paramref name="tradeDate"/>. An
    /// unreported time on a same-day filing is treated as available: the session's own trading is
    /// what the metric describes, and refusing every same-day filing would delete the live run's
    /// own catalysts.
    /// </summary>
    public static bool IsAvailableBy(StockDisclosure disclosure, DateOnly tradeDate, TimeOnly? cutoff = null)
    {
        if (disclosure.Date > tradeDate)
        {
            return false;
        }

        if (disclosure.Date < tradeDate)
        {
            return true;
        }

        return disclosure.FiledAtPht is not TimeOnly filedAt || filedAt <= (cutoff ?? PseTradingSessionRules.MarketClose);
    }

    /// <summary>
    /// Wilder's RSI. Returns null until <paramref name="period"/> + 1 closes exist.
    /// </summary>
    public static decimal? ComputeRsi(IReadOnlyList<decimal> closes, int period = RsiPeriod)
    {
        if (period <= 0 || closes.Count < period + 1)
        {
            return null;
        }

        decimal gainSum = 0m;
        decimal lossSum = 0m;

        for (var i = 1; i <= period; i++)
        {
            var diff = closes[i] - closes[i - 1];
            if (diff > 0m)
            {
                gainSum += diff;
            }
            else
            {
                lossSum -= diff;
            }
        }

        var avgGain = gainSum / period;
        var avgLoss = lossSum / period;

        for (var i = period + 1; i < closes.Count; i++)
        {
            var diff = closes[i] - closes[i - 1];
            var gain = diff > 0m ? diff : 0m;
            var loss = diff < 0m ? -diff : 0m;
            avgGain = ((avgGain * (period - 1)) + gain) / period;
            avgLoss = ((avgLoss * (period - 1)) + loss) / period;
        }

        if (avgLoss == 0m)
        {
            return avgGain == 0m ? 50m : 100m;
        }

        var rs = avgGain / avgLoss;
        return 100m - (100m / (1m + rs));
    }

    /// <summary>
    /// EMA seeded with the initial <paramref name="period"/>-bar SMA (standard
    /// trading-platform convention).
    /// </summary>
    public static decimal? ComputeEma(IReadOnlyList<decimal> values, int period = EmaPeriod)
    {
        if (period <= 0 || values.Count < period)
        {
            return null;
        }

        var seed = 0m;
        for (var i = 0; i < period; i++)
        {
            seed += values[i];
        }

        var ema = seed / period;
        var multiplier = 2m / (period + 1m);

        for (var i = period; i < values.Count; i++)
        {
            ema = (values[i] * multiplier) + (ema * (1m - multiplier));
        }

        return ema;
    }

    /// <summary>
    /// Simple moving average over the most recent <paramref name="period"/> values.
    /// </summary>
    public static decimal? ComputeSma(IReadOnlyList<decimal> values, int period)
    {
        if (period <= 0 || values.Count < period)
        {
            return null;
        }

        var sum = 0m;
        for (var i = values.Count - period; i < values.Count; i++)
        {
            sum += values[i];
        }

        return sum / period;
    }

    /// <summary>
    /// Wilder's Average True Range (smoothed true range).
    /// </summary>
    public static decimal? ComputeAtr(IReadOnlyList<OhlcvBar> bars, int period = AtrPeriod)
    {
        if (period <= 0 || bars.Count < period + 1)
        {
            return null;
        }

        var trueRanges = new List<decimal>(bars.Count - 1);

        for (var i = 1; i < bars.Count; i++)
        {
            var high = bars[i].High;
            var low = bars[i].Low;
            var previousClose = bars[i - 1].Close;

            var trueRange = Math.Max(
                high - low,
                Math.Max(Math.Abs(high - previousClose), Math.Abs(low - previousClose)));

            trueRanges.Add(trueRange);
        }

        var atr = 0m;
        for (var i = 0; i < period; i++)
        {
            atr += trueRanges[i];
        }

        atr /= period;

        for (var i = period; i < trueRanges.Count; i++)
        {
            atr = ((atr * (period - 1)) + trueRanges[i]) / period;
        }

        return atr;
    }

    /// <summary>
    /// ATR14 as a percentage of the latest close: the daily volatility budget a stop has
    /// to survive. Null when the series is too short.
    /// </summary>
    public static decimal? ComputeAtrPct(IReadOnlyList<OhlcvBar> bars)
    {
        if (bars.Count == 0)
        {
            return null;
        }

        var atr = ComputeAtr(bars);
        var close = bars[^1].Close;

        if (atr is not decimal value || value <= 0m || close <= 0m)
        {
            return null;
        }

        return decimal.Round(value / close * 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Short-term ATR(5) over the last six sessions divided by ATR(14). A ratio below 0.85
    /// is a volatility contraction (VCP-style base); above 1.15 the range is expanding.
    /// </summary>
    public static decimal? ComputeVolatilityContractionRatio(IReadOnlyList<OhlcvBar> bars)
    {
        if (bars.Count < AtrPeriod + 1)
        {
            return null;
        }

        var shortAtr = ComputeAtr(bars.TakeLast(6).ToList(), 5);
        var longAtr = ComputeAtr(bars);

        if (shortAtr is not decimal shortValue || longAtr is not decimal longValue ||
            shortValue <= 0m || longValue <= 0m)
        {
            return null;
        }

        return decimal.Round(shortValue / longValue, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// 63-session return difference versus the PSEi, in percentage points. Positive means
    /// the name is outperforming the index. Null when either series is too short.
    /// </summary>
    public static decimal? ComputeRelativeStrength63d(
        IReadOnlyList<OhlcvBar> stockBars,
        IReadOnlyList<OhlcvBar>? indexBars,
        int period = 63)
    {
        if (period <= 0 ||
            stockBars.Count <= period ||
            indexBars is null ||
            indexBars.Count <= period)
        {
            return null;
        }

        var stockStart = stockBars[^(period + 1)].Close;
        var stockEnd = stockBars[^1].Close;
        var indexStart = indexBars[^(period + 1)].Close;
        var indexEnd = indexBars[^1].Close;

        if (stockStart <= 0m || indexStart <= 0m)
        {
            return null;
        }

        var stockReturn = (stockEnd - stockStart) / stockStart * 100m;
        var indexReturn = (indexEnd - indexStart) / indexStart * 100m;

        return decimal.Round(stockReturn - indexReturn, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Up-volume divided by down-volume over the last <paramref name="period"/> sessions.
    /// This is the desk's substitute for net foreign flow (which no public PSE feed
    /// publishes): volume is accumulating on advancing sessions when the ratio exceeds ~1.25.
    /// The ratio is capped at 10 so a twenty-session run with almost no down volume cannot
    /// produce an absurd number; a window with no volume at all returns null (unreported).
    /// </summary>
    public static decimal? ComputeUpDownVolumeRatio(IReadOnlyList<OhlcvBar> bars, int period = MoneyFlowPeriod)
    {
        if (period <= 0 || bars.Count < period + 1)
        {
            return null;
        }

        var upVolume = 0m;
        var downVolume = 0m;

        for (var index = bars.Count - period; index < bars.Count; index++)
        {
            var change = bars[index].Close - bars[index - 1].Close;

            if (change > 0m)
            {
                upVolume += bars[index].Volume;
            }
            else if (change < 0m)
            {
                downVolume += bars[index].Volume;
            }
        }

        if (upVolume <= 0m && downVolume <= 0m)
        {
            return null;
        }

        if (downVolume <= 0m)
        {
            return 10m;
        }

        return decimal.Round(Math.Min(10m, upVolume / downVolume), 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Chaikin Money Flow over <paramref name="period"/> sessions: the volume-weighted
    /// closing location value, in <c>[-1, 1]</c>. Positive readings mean the session closes
    /// are happening near the highs even when the headline price is flat — the quiet
    /// accumulation footprint a foreign-flow column would have shown. Sessions with a zero
    /// high-low range (limit-locked) carry no location information and are left out of both
    /// numerator and denominator; a window with no usable volume returns null.
    /// </summary>
    public static decimal? ComputeChaikinMoneyFlow(IReadOnlyList<OhlcvBar> bars, int period = MoneyFlowPeriod)
    {
        if (period <= 0 || bars.Count < period)
        {
            return null;
        }

        var moneyFlowVolume = 0m;
        var volume = 0m;

        for (var index = bars.Count - period; index < bars.Count; index++)
        {
            var bar = bars[index];
            var range = bar.High - bar.Low;

            if (range <= 0m || bar.Volume <= 0)
            {
                continue;
            }

            var location = ((bar.Close - bar.Low) - (bar.High - bar.Close)) / range;
            moneyFlowVolume += location * bar.Volume;
            volume += bar.Volume;
        }

        if (volume <= 0m)
        {
            return null;
        }

        return decimal.Round(Math.Clamp(moneyFlowVolume / volume, -1m, 1m), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Kaufman's efficiency ratio over <paramref name="period"/> sessions: the net move
    /// divided by the sum of absolute session-to-session moves, in <c>[0, 1]</c>. A value near
    /// 1 is a smooth institutional trend; near 0 it is chop that pays the round trip without
    /// going anywhere. Null when history is short or the price never moved.
    /// </summary>
    public static decimal? ComputeTrendEfficiency(IReadOnlyList<OhlcvBar> bars, int period = TrendEfficiencyPeriod)
    {
        if (period <= 0 || bars.Count < period + 1)
        {
            return null;
        }

        var window = bars.Skip(bars.Count - (period + 1)).ToList();
        var path = 0m;

        for (var index = 1; index < window.Count; index++)
        {
            path += Math.Abs(window[index].Close - window[index - 1].Close);
        }

        if (path <= 0m)
        {
            return null;
        }

        var netMove = Math.Abs(window[^1].Close - window[0].Close);

        return decimal.Round(Math.Clamp(netMove / path, 0m, 1m), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Empirical beta of the ticker against the PSEi over up to <paramref name="period"/>
    /// sessions of <b>overlapping</b> dates only. Index bars are matched by date, so a symbol
    /// that skipped a session is never paired with the wrong index close. Null when the index
    /// series is missing or fewer than 60 overlapping returns exist.
    /// </summary>
    public static decimal? ComputeBeta(
        IReadOnlyList<OhlcvBar> stockBars,
        IReadOnlyList<OhlcvBar>? indexBars,
        int period = BetaPeriod)
    {
        if (indexBars is null || indexBars.Count < 2 || period < 2)
        {
            return null;
        }

        var indexCloses = new Dictionary<DateOnly, decimal>(indexBars.Count);

        foreach (var bar in indexBars)
        {
            indexCloses[bar.Date] = bar.Close;
        }

        var stock = new List<decimal>();
        var index = new List<decimal>();

        foreach (var bar in stockBars)
        {
            if (indexCloses.TryGetValue(bar.Date, out var close))
            {
                stock.Add(bar.Close);
                index.Add(close);
            }
        }

        if (stock.Count < 61)
        {
            return null;
        }

        var start = Math.Max(1, stock.Count - Math.Min(period, stock.Count - 1));
        var count = 0;
        var meanStock = 0m;
        var meanIndex = 0m;
        var stockReturns = new List<decimal>(stock.Count);
        var indexReturns = new List<decimal>(stock.Count);

        for (var position = start; position < stock.Count; position++)
        {
            var previousStock = stock[position - 1];
            var previousIndex = index[position - 1];

            if (previousStock <= 0m || previousIndex <= 0m)
            {
                continue;
            }

            var stockReturn = (stock[position] - previousStock) / previousStock;
            var indexReturn = (index[position] - previousIndex) / previousIndex;

            stockReturns.Add(stockReturn);
            indexReturns.Add(indexReturn);
            meanStock += stockReturn;
            meanIndex += indexReturn;
            count++;
        }

        if (count < 60)
        {
            return null;
        }

        meanStock /= count;
        meanIndex /= count;

        var covariance = 0m;
        var variance = 0m;

        for (var position = 0; position < count; position++)
        {
            var stockDeviation = stockReturns[position] - meanStock;
            var indexDeviation = indexReturns[position] - meanIndex;

            covariance += stockDeviation * indexDeviation;
            variance += indexDeviation * indexDeviation;
        }

        if (variance <= 0m)
        {
            return null;
        }

        return decimal.Round(covariance / variance, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Worst peak-to-trough close drawdown over the trailing <paramref name="period"/>
    /// sessions, as a positive percentage. The YEAR sleeve uses it to separate quality
    /// compounders from names that only look cheap after a halving. Null under 61 sessions.
    /// </summary>
    public static decimal? ComputeMaxDrawdownPct(IReadOnlyList<decimal> closes, int period = BetaPeriod)
    {
        if (closes.Count < 61)
        {
            return null;
        }

        var start = Math.Max(0, closes.Count - period);
        var peak = closes[start];
        var worst = 0m;

        for (var index = start + 1; index < closes.Count; index++)
        {
            var close = closes[index];

            if (close > peak)
            {
                peak = close;
                continue;
            }

            if (peak <= 0m)
            {
                continue;
            }

            var drawdown = (peak - close) / peak * 100m;

            if (drawdown > worst)
            {
                worst = drawdown;
            }
        }

        return decimal.Round(worst, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Annualised realised volatility of daily close-to-close returns over
    /// <paramref name="period"/> sessions, in percent. This is the regime dial the risk
    /// governor reads: a 30%-vol tape deserves smaller clips than a 12%-vol one even when the
    /// trend is identical. Null when the window is not fully populated.
    /// </summary>
    public static decimal? ComputeRealizedVolatilityPct(
        IReadOnlyList<decimal> closes,
        int period = MoneyFlowPeriod)
    {
        if (period < 2 || closes.Count < period + 1)
        {
            return null;
        }

        var returns = new List<decimal>(period);
        var start = closes.Count - period;

        for (var index = start; index < closes.Count; index++)
        {
            var previous = closes[index - 1];

            if (previous <= 0m)
            {
                continue;
            }

            returns.Add((closes[index] - previous) / previous);
        }

        if (returns.Count < 2)
        {
            return null;
        }

        var mean = returns.Average();
        var variance = 0m;

        foreach (var value in returns)
        {
            var deviation = value - mean;
            variance += deviation * deviation;
        }

        variance /= returns.Count - 1;

        var annualised = Math.Sqrt((double)variance) * Math.Sqrt(252d) * 100d;
        return decimal.Round((decimal)annualised, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Participation of the evaluated universe: the percentage above its own 50D and 200D
    /// averages plus the Stage 2 count. An empty sample returns null percentages rather
    /// than a misleading 0%.
    /// </summary>
    public static MarketBreadth ComputeMarketBreadth(IReadOnlyList<StockDailyMetric> metrics)
    {
        if (metrics is null || metrics.Count == 0)
        {
            return new MarketBreadth(null, null, 0, 0);
        }

        var above50 = metrics.Count(metric => metric.Sma50 > 0m && metric.ClosePrice >= metric.Sma50);
        var above200 = metrics.Count(metric => metric.Sma200 > 0m && metric.ClosePrice >= metric.Sma200);
        var stage2 = metrics.Count(metric => string.Equals(metric.WeinsteinStage, "STAGE_2", StringComparison.Ordinal));

        return new MarketBreadth(
            decimal.Round((decimal)above50 / metrics.Count * 100m, 1, MidpointRounding.AwayFromZero),
            decimal.Round((decimal)above200 / metrics.Count * 100m, 1, MidpointRounding.AwayFromZero),
            stage2,
            metrics.Count);
    }

    /// <summary>
    /// Highest close of the trailing 52 weeks (or of the whole series when it is
    /// shorter). The candidate floor already guarantees 200 sessions.
    /// </summary>
    public static decimal? ComputeWeek52High(IReadOnlyList<decimal> closes)
    {
        if (closes.Count == 0)
        {
            return null;
        }

        var start = Math.Max(0, closes.Count - Week52Period);
        var high = closes[start];

        for (var index = start + 1; index < closes.Count; index++)
        {
            if (closes[index] > high)
            {
                high = closes[index];
            }
        }

        return high;
    }

    /// <summary>
    /// Weinstein stage analysis on the 30-week (150D) average: STAGE_1 basing,
    /// STAGE_2 advancing (the only long-only zone), STAGE_3 distribution and STAGE_4
    /// declining. Returns null until a 20-session slope can be measured.
    /// </summary>
    public static string? ComputeWeinsteinStage(IReadOnlyList<decimal> closes)
    {
        if (closes.Count < WeinsteinPeriod + WeinsteinSlopeLookback)
        {
            return null;
        }

        var currentAverage = ComputeSma(closes, WeinsteinPeriod);
        var priorAverage = ComputeSma(closes.Take(closes.Count - WeinsteinSlopeLookback).ToList(), WeinsteinPeriod);

        if (currentAverage is not decimal current || priorAverage is not decimal prior)
        {
            return null;
        }

        var rising = current > prior * 1.005m;
        var falling = current < prior * 0.995m;
        var aboveAverage = closes[^1] >= current;

        if (falling)
        {
            return "STAGE_4";
        }

        if (rising && aboveAverage)
        {
            return "STAGE_2";
        }

        return aboveAverage ? "STAGE_3" : "STAGE_1";
    }

    /// <summary>
    /// Weighted 0-100 pre-score: trend alignment (30), RSI band (20), volume
    /// expansion (15), foreign-flow tilt (15) and structure proximity (20).
    /// When foreign flow is unreported the verified 85-point technical score is
    /// normalised to 0-100 instead of awarding phantom foreign-flow points.
    /// </summary>
    public static int ComputePreScore(
        decimal close,
        decimal ema20,
        decimal sma50,
        decimal sma200,
        decimal rsi,
        decimal volumeRatio,
        decimal avgTurnover20dPhp,
        decimal? netForeignTodayPhp,
        decimal atr,
        decimal support,
        decimal resistance)
    {
        decimal score = 0m;

        if (close > ema20)
        {
            score += 10m;
        }

        if (ema20 > sma50)
        {
            score += 10m;
        }

        if (sma50 > sma200)
        {
            score += 10m;
        }

        if (rsi >= 45m && rsi <= 68m)
        {
            score += 20m;
        }
        else if (rsi >= 40m && rsi < 45m)
        {
            score += 12m;
        }
        else if (rsi > 68m && rsi <= 72m)
        {
            score += 10m;
        }
        else if (rsi >= 35m && rsi < 40m)
        {
            score += 5m;
        }
        else if (rsi > 72m)
        {
            score += 3m;
        }

        if (volumeRatio >= 1.5m)
        {
            score += 15m;
        }
        else if (volumeRatio >= 1.2m)
        {
            score += 10m;
        }
        else if (volumeRatio >= 0.8m)
        {
            score += 6m;
        }

        if (netForeignTodayPhp is decimal netForeign && avgTurnover20dPhp > 0m)
        {
            var tilt = netForeign / avgTurnover20dPhp;
            if (tilt >= 0.05m)
            {
                score += 15m;
            }
            else if (tilt > 0m)
            {
                score += 10m;
            }
            else if (tilt == 0m)
            {
                score += 5m;
            }
        }

        if (atr > 0m)
        {
            var rewardRoom = (resistance - close) / atr;
            if (rewardRoom >= 1.5m && rewardRoom <= 4m)
            {
                score += 10m;
            }
            else if (rewardRoom > 0m)
            {
                score += 5m;
            }

            var supportDistance = (close - support) / atr;
            if (supportDistance >= 0m && supportDistance <= 2m)
            {
                score += 10m;
            }
            else if (supportDistance > 2m && supportDistance <= 3.5m)
            {
                score += 5m;
            }
        }

        if (netForeignTodayPhp is null)
        {
            // Net foreign flow is only reported when an official PSE Daily Quotation Report was
            // parsed for the session (live template or operator upload). Without it the foreign
            // pillar (15 of 100) is unverifiable, so the fully verified 85-point technical score
            // is normalised to a comparable 0-100 scale instead of awarding phantom points.
            var technical = Math.Clamp(score, 0m, TechnicalScoreMaximum);
            return (int)decimal.Round(
                technical * 100m / TechnicalScoreMaximum,
                0,
                MidpointRounding.AwayFromZero);
        }

        var clamped = Math.Clamp(score, 0m, 100m);
        return (int)decimal.Round(clamped, 0, MidpointRounding.AwayFromZero);
    }
}

