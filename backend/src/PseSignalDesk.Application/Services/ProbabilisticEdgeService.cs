using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Everything the edge calculator needs about one planned trade plus the tape and
/// structure context that should move the probability estimate.
/// </summary>
public sealed record SetupEdgeRequest(
    decimal EntryPrice,
    decimal StopLoss,
    decimal TargetPrice,
    decimal ConvictionScore,
    MarketRegime MarketRegime,
    HorizonProfile Profile,
    decimal? EmpiricalWinRate = null,
    decimal? RelativeStrength63d = null,
    decimal? VolatilityContractionRatio = null,
    string? WeinsteinStage = null,
    decimal? BreadthAboveSma50Pct = null,
    IReadOnlyList<PseTrapFlag>? TrapFlags = null,
    decimal? AtrPct = null,
    decimal? TrailingDividendYieldPct = null,
    // Money-manager context: the sector rotation read, the round-trip tick spread the tape
    // charges, the accumulation footprint, a walk-forward calibration offset and the all-in
    // fee rate once slippage is priced. All optional so older callers keep their numbers.
    string? SectorQuadrant = null,
    decimal? TickSpreadPct = null,
    decimal? UpDownVolumeRatio20d = null,
    decimal? CalibrationOffsetPct = null,
    decimal? SlippageInclusiveFeeRate = null,
    // Official PSE Daily Quotation Report flow plus the global rate dial. All optional:
    // a session without the DQR or the macro feed prices exactly as it did before.
    decimal? NetForeignTurnoverRatio = null,
    decimal? Us10YClosePct = null,
    string? Sector = null,
    // The promoted per-horizon money-manager model plus the twelve live factors it was
    // trained on. Both null keeps the deterministic prior in charge, exactly as before.
    LearnedEdgeModel? LearnedModel = null,
    decimal?[]? LearnedFeatures = null,
    // The empirical payoff distribution behind the hit rate. Null keeps the textbook
    // "a win pays the full net reward, a loss costs a full R" assumption; a supplied sample
    // prices expected value at the payoffs this ticker actually delivered.
    BarrierStatistics? BarrierStats = null);

/// <summary>
/// Server-owned probability view of one setup: fee-adjusted odds, calibrated win
/// probability, expected value in R, the Quarter-Kelly stake and the verdict.
/// </summary>
public sealed record EdgeAssessment(
    decimal NetRiskReward,
    decimal WinProbability,
    decimal BreakEvenWinRate,
    decimal ExpectedValueR,
    decimal QuarterKellyFraction,
    EdgeVerdict Verdict)
{
    /// <summary>
    /// Probability points contributed by the promoted money-manager model. Zero whenever no
    /// learned model was supplied or the model's gate has not been earned yet.
    /// </summary>
    public decimal LearnedEdgeTilt { get; init; }

    /// <summary>
    /// Average R lost on a non-winning trial, when the caller supplied an empirical payoff
    /// sample. Exactly <c>1</c> (a full stop) when it did not: that is the conservative default.
    /// </summary>
    public decimal ExpectedLossR { get; init; } = 1m;

    /// <summary>Share of the sample's windows that expired without touching either barrier.</summary>
    public decimal ExpiryRate { get; init; }
}

/// <summary>
/// The payoff distribution behind a base rate. A hit rate alone cannot price a bet: a window
/// that expires <c>+0.01R</c> in profit is not the same event as a target hit at <c>+1.5R</c>, and
/// charging the full net reward for it inflates expected value by the gap between the two.
/// These are the conditional payoffs the desk actually realized on the same ticker, in R units
/// of each trial's own fee-paid risk.
/// </summary>
public sealed record BarrierStatistics(
    int Trials,
    int Wins,
    int TargetHits,
    int StopOuts,
    int Expiries,
    decimal WinRate,
    decimal AverageWinR,
    decimal AverageLossR)
{
    /// <summary>An empty sample. Callers must treat it as "unreported", never as zero odds.</summary>
    public static BarrierStatistics None { get; } = new(0, 0, 0, 0, 0, 0m, 0m, 0m);

    /// <summary>Share of resolved trials that never reached either barrier.</summary>
    public decimal ExpiryRate => Trials <= 0 ? 0m : decimal.Round((decimal)Expiries / Trials, 4, MidpointRounding.AwayFromZero);
}

/// <summary>
/// Deterministic "most educated guess" engine. It never calls a model or a network:
/// the empirical base rate comes from the ticker's own OHLCV history, the probability
/// combines barrier geometry with that base rate, and the verdict is a pure function of
/// the resulting expected value. A target the market cannot realistically reach lowers
/// the probability instead of being rewarded for a big gross number.
/// </summary>
public sealed class ProbabilisticEdgeService
{
    /// <summary>Neutral prior win rate used to shrink small empirical samples.</summary>
    public const decimal NeutralPriorWinRate = 0.42m;

    /// <summary>Pseudo-observations of the prior, so five lucky trades cannot claim an 80% edge.</summary>
    public const int PriorSampleSize = 12;

    public const decimal StopAtrMultiple = 1.5m;
    public const decimal TargetAtrMultiple = 3.0m;
    public const int BarrierWindowBars = 10;
    public const int MinimumHistoryBars = 60;

    /// <summary>
    /// Market-wide net foreign selling at or below this level (PHP -500M) is treated as a
    /// distribution day that deserves extra cash, not as a buying opportunity.
    /// </summary>
    public const decimal MarketOutflowCashThresholdPhp = -500_000_000m;

    /// <summary>
    /// Rolling historical hit rate for trend-aligned, ATR-barrier setups on one ticker,
    /// using the WEEK profile's barrier geometry. Kept for backward compatibility; prefer
    /// the <see cref="HorizonProfile"/> overload so each book is graded against the same
    /// stop/target multiples and forward window it actually trades.
    /// </summary>
    public static decimal? ComputeEmpiricalBaseRate(IReadOnlyList<OhlcvBar> bars) =>
        ComputeEmpiricalBaseRate(bars, HorizonProfile.For(SignalHorizon.WEEK));

    /// <summary>
    /// Rolling historical hit rate for trend-aligned, ATR-barrier setups on one ticker,
    /// shaped by <paramref name="profile"/>: the stop/target multiples and the forward
    /// window are the horizon's own, so a DAY base rate describes a 1.2x/2.6x/3-bar bet
    /// rather than a WEEK swing. Ambiguous same-session bars count as a stop-out
    /// (conservative), time-expired exits are netted for the per-leg round trip (0.690%), and the
    /// result is shrunk toward <see cref="NeutralPriorWinRate"/>. Returns null when there
    /// are no qualifying trials.
    /// </summary>
    public static decimal? ComputeEmpiricalBaseRate(IReadOnlyList<OhlcvBar> bars, HorizonProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (bars is null || bars.Count < MinimumHistoryBars)
        {
            return null;
        }

        var stats = ComputeBarrierStatistics(bars, ComputeSeries(bars), profile);

        return stats.Trials == 0 ? null : stats.WinRate;
    }

    /// <summary>
    /// The full payoff distribution behind one horizon's base rate, priced with the friction that
    /// applied on each trial's own entry date and with gap-through-stop fills taken at the open.
    /// </summary>
    public static BarrierStatistics ComputeBarrierStatistics(IReadOnlyList<OhlcvBar> bars, HorizonProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        if (bars is null || bars.Count < MinimumHistoryBars)
        {
            return BarrierStatistics.None;
        }

        return ComputeBarrierStatistics(bars, ComputeSeries(bars), profile);
    }

    /// <summary>Every horizon the desk prices, in the order the sleeves are reported.</summary>
    public static IReadOnlyList<SignalHorizon> AllHorizons { get; } = new[]
    {
        SignalHorizon.DAY,
        SignalHorizon.WEEK,
        SignalHorizon.MONTH,
        SignalHorizon.YEAR
    };

    /// <summary>
    /// All four horizon base rates in <b>one</b> pass. The ATR and 50D series are built once
    /// (previously every trial rebuilt the prefix and re-ran Wilder smoothing), so pricing the
    /// DAY, WEEK, MONTH and YEAR books costs one sweep of the ticker's history instead of four
    /// quadratic ones.
    /// </summary>
    public static IReadOnlyDictionary<SignalHorizon, decimal?> ComputeHorizonBaseRates(IReadOnlyList<OhlcvBar> bars)
    {
        var rates = new Dictionary<SignalHorizon, decimal?>(AllHorizons.Count);

        if (bars is null || bars.Count < MinimumHistoryBars)
        {
            foreach (var horizon in AllHorizons)
            {
                rates[horizon] = null;
            }

            return rates;
        }

        var series = ComputeSeries(bars);

        foreach (var horizon in AllHorizons)
        {
            rates[horizon] = ComputeBaseRate(bars, series, HorizonProfile.For(horizon));
        }

        return rates;
    }

    private sealed record IndicatorSeries(decimal?[] Atr, decimal?[] Sma50);

    /// <summary>
    /// Prefix indicators for the trial sweep. <c>atr[i]</c> and <c>sma50[i]</c> equal what
    /// <see cref="QuantitativeFilterService.ComputeAtr"/> and
    /// <see cref="QuantitativeFilterService.ComputeSma"/> return for the history ending at
    /// bar <c>i</c>, but they are extended incrementally instead of recomputed per trial.
    /// </summary>
    private static IndicatorSeries ComputeSeries(IReadOnlyList<OhlcvBar> bars)
    {
        var atr = new decimal?[bars.Count];
        var sma50 = new decimal?[bars.Count];
        var trueRange = new decimal[bars.Count];

        for (var index = 1; index < bars.Count; index++)
        {
            var high = bars[index].High;
            var low = bars[index].Low;
            var previousClose = bars[index - 1].Close;

            trueRange[index] = Math.Max(
                high - low,
                Math.Max(Math.Abs(high - previousClose), Math.Abs(low - previousClose)));
        }

        var atrPeriod = QuantitativeFilterService.AtrPeriod;

        if (bars.Count > atrPeriod)
        {
            var running = 0m;

            for (var index = 1; index <= atrPeriod; index++)
            {
                running += trueRange[index];
            }

            running /= atrPeriod;
            atr[atrPeriod] = running;

            for (var index = atrPeriod + 1; index < bars.Count; index++)
            {
                running = ((running * (atrPeriod - 1)) + trueRange[index]) / atrPeriod;
                atr[index] = running;
            }
        }

        var smaPeriod = QuantitativeFilterService.SmaShortPeriod;

        if (bars.Count >= smaPeriod)
        {
            var running = 0m;

            for (var index = 0; index < smaPeriod; index++)
            {
                running += bars[index].Close;
            }

            sma50[smaPeriod - 1] = running / smaPeriod;

            for (var index = smaPeriod; index < bars.Count; index++)
            {
                running += bars[index].Close - bars[index - smaPeriod].Close;
                sma50[index] = running / smaPeriod;
            }
        }

        return new IndicatorSeries(atr, sma50);
    }

    private static decimal? ComputeBaseRate(
        IReadOnlyList<OhlcvBar> bars,
        IndicatorSeries series,
        HorizonProfile profile)
    {
        var stats = ComputeBarrierStatistics(bars, series, profile);

        return stats.Trials == 0 ? null : stats.WinRate;
    }

    /// <summary>
    /// Rolls the horizon's own barriers over the ticker's history and records what each trial
    /// paid. Three things differ from a naive hit-rate sweep:
    /// <list type="bullet">
    /// <item>friction is the statutory round trip <b>as it applied on that trial's date</b>
    /// (<c>1.190%</c> before CMEPA, <c>0.690%</c> after), so a 2023 trial is not credited with
    /// 2025's cheaper tax;</item>
    /// <item>a session that opens through the stop fills at the open, not at the stop, which is
    /// what the order would really have paid;</item>
    /// <item>an expired window is recorded with the R it actually delivered instead of being
    /// scored as a full target hit.</item>
    /// </list>
    /// </summary>
    private static BarrierStatistics ComputeBarrierStatistics(
        IReadOnlyList<OhlcvBar> bars,
        IndicatorSeries series,
        HorizonProfile profile)
    {
        var stopMultiple = profile.AtrStopMultiplier > 0m ? profile.AtrStopMultiplier : StopAtrMultiple;
        var targetMultiple = profile.AtrTargetMultiplier > 0m ? profile.AtrTargetMultiplier : TargetAtrMultiple;
        var barrierWindow = Math.Max(1, profile.ForwardWindowBars);

        var trials = 0;
        var wins = 0;
        var targetHits = 0;
        var stopOuts = 0;
        var expiries = 0;
        var winSum = 0m;
        var lossSum = 0m;
        var losses = 0;

        for (var index = 50; index + barrierWindow < bars.Count; index++)
        {
            if (series.Atr[index] is not decimal atrValue || atrValue <= 0m)
            {
                continue;
            }

            var entry = bars[index].Close;
            if (entry <= 0m || series.Sma50[index] is not decimal average || entry <= average)
            {
                // Only trend-aligned entries belong in this ticker's base rate.
                continue;
            }

            var stop = entry - (stopMultiple * atrValue);
            var target = entry + (targetMultiple * atrValue);
            if (stop <= 0m || !PseBoardLotService.IsTradablePrice(entry))
            {
                continue;
            }

            var tradeDate = bars[index].Date;
            var feeRate = PseBoardLotService.RoundTripFeeRateOn(tradeDate);
            var netRisk = PseBoardLotService.ComputeNetRiskPerShare(entry, stop, feeRate);
            if (netRisk <= 0m)
            {
                continue;
            }

            var (buyRate, sellRate) = PseBoardLotService.SplitRoundTripFee(feeRate, tradeDate);
            decimal? realizedR = null;

            for (var forward = index + 1; forward <= index + barrierWindow; forward++)
            {
                var bar = bars[forward];

                // A session that gaps through the stop fills at its open; the same-session
                // target/stop ambiguity still resolves as a stop (conservative).
                if (PseBoardLotService.ResolveStopFillPrice(stop, bar.Open, bar.Low) is decimal stopFill)
                {
                    stopOuts++;
                    realizedR = RealizedR(entry, stopFill, buyRate, sellRate, netRisk);
                    break;
                }

                if (bar.High >= target)
                {
                    targetHits++;
                    realizedR = RealizedR(entry, target, buyRate, sellRate, netRisk);
                    break;
                }
            }

            if (realizedR is null)
            {
                expiries++;
                realizedR = RealizedR(entry, bars[index + barrierWindow].Close, buyRate, sellRate, netRisk);
            }

            trials++;

            if (realizedR > 0m)
            {
                wins++;
                winSum += realizedR.Value;
            }
            else
            {
                losses++;
                lossSum += Math.Abs(realizedR.Value);
            }
        }

        if (trials == 0)
        {
            return BarrierStatistics.None;
        }

        var shrunk = (wins + (NeutralPriorWinRate * PriorSampleSize)) / (trials + PriorSampleSize);

        return new BarrierStatistics(
            trials,
            wins,
            targetHits,
            stopOuts,
            expiries,
            decimal.Round(shrunk, 4, MidpointRounding.AwayFromZero),
            wins == 0 ? 0m : decimal.Round(winSum / wins, 4, MidpointRounding.AwayFromZero),
            losses == 0 ? 0m : decimal.Round(lossSum / losses, 4, MidpointRounding.AwayFromZero));
    }

    /// <summary>Net R of one simulated exit, using each leg's own date-correct rate.</summary>
    private static decimal RealizedR(
        decimal entry,
        decimal exit,
        decimal buyRate,
        decimal sellRate,
        decimal netRiskPerShare)
    {
        if (exit <= 0m || netRiskPerShare <= 0m)
        {
            return 0m;
        }

        var netPerShare = (exit * (1m - sellRate)) - (entry * (1m + buyRate));

        return netPerShare / netRiskPerShare;
    }

    /// <summary>
    /// Scores one plan: net R:R, break-even win rate, calibrated win probability,
    /// expected value in R, Quarter-Kelly stake and the TRADE / SMALL_SIZE / WATCH_ONLY
    /// verdict. Pure and deterministic, so the same inputs always produce the same bet.
    /// </summary>
    public EdgeAssessment EvaluateSetup(SetupEdgeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Slippage-aware friction: when the caller has priced the tick spread and market impact
        // for this size, charge the same all-in rate the order ticket charges; otherwise fall
        // back to the statutory 0.690% round trip. Either way each leg pays its own rate inside
        // PseBoardLotService, so a fee-only plan at PHP 100/PHP 95/PHP 115 prints 2.51:1.
        var feeRate = request.SlippageInclusiveFeeRate is decimal suppliedRate &&
                      suppliedRate >= PseBoardLotService.RoundTripFeeRate
            ? suppliedRate
            : PseBoardLotService.RoundTripFeeRate;

        var netReward = PseBoardLotService.ComputeNetRewardPerShare(
            request.EntryPrice,
            request.TargetPrice,
            feeRate);
        var netRisk = PseBoardLotService.ComputeNetRiskPerShare(
            request.EntryPrice,
            request.StopLoss,
            feeRate);
        var breakEven = PseBoardLotService.ComputeBreakEvenWinRate(
            request.EntryPrice,
            request.StopLoss,
            request.TargetPrice,
            feeRate);

        if (netReward <= 0m || netRisk <= 0m)
        {
            return new EdgeAssessment(0m, 0m, breakEven, 0m, 0m, EdgeVerdict.WATCH_ONLY);
        }

        var netRiskReward = decimal.Round(netReward / netRisk, 2, MidpointRounding.AwayFromZero);

        // Barrier geometry: a target three times further away than the stop is hit first
        // far less often than a tight one. This is what makes an unreachable target
        // lower P(win) instead of inflating the gross ratio.
        var geometry = netRisk / (netReward + netRisk);
        var empirical = request.EmpiricalWinRate ?? NeutralPriorWinRate;
        var probability = (empirical + geometry) / 2m;

        probability += request.WeinsteinStage switch
        {
            "STAGE_2" => 0.02m,
            "STAGE_4" => -0.03m,
            _ => 0m
        };

        if (request.RelativeStrength63d is decimal relativeStrength)
        {
            probability += relativeStrength switch
            {
                >= 2m => 0.03m,
                > 0m => 0.015m,
                <= -2m => -0.03m,
                _ => -0.01m
            };
        }

        if (request.VolatilityContractionRatio is decimal contraction)
        {
            probability += contraction switch
            {
                < 0.85m => 0.015m,
                > 1.15m => -0.015m,
                _ => 0m
            };
        }

        if (request.BreadthAboveSma50Pct is decimal breadth)
        {
            probability += breadth switch
            {
                >= 60m => 0.01m,
                <= 35m => -0.02m,
                _ => 0m
            };
        }

        probability += request.MarketRegime switch
        {
            MarketRegime.BULLISH => 0.01m,
            MarketRegime.BEARISH => -0.04m,
            _ => 0m
        };

        // Fee-drag reality check: when the round trip is at least half the session's ATR, a
        // short-horizon stop has to survive noise that costs more than the move can pay. A
        // chopped 1%-range tape is not a DAY/WEEK edge no matter how clean the chart.
        if (request.AtrPct is decimal atrPct &&
            atrPct > 0m &&
            request.Profile.Horizon is SignalHorizon.DAY or SignalHorizon.WEEK &&
            (feeRate * 100m) >= atrPct * 0.5m)
        {
            probability -= 0.025m;
        }

        // Income tilt: a MONTH/YEAR position in a Stage 1/2 base that pays at least 4% cash
        // yield has a total-return tailwind the pure price geometry cannot see.
        if (request.TrailingDividendYieldPct is decimal dividendYield &&
            dividendYield >= 4.0m &&
            request.Profile.Horizon is SignalHorizon.MONTH or SignalHorizon.YEAR &&
            request.WeinsteinStage is "STAGE_2" or "STAGE_1")
        {
            probability += 0.015m;
        }

        // Sector rotation: money flowing into the name's own cluster is a tailwind that a
        // single-stock chart cannot show. Lagging sectors cost more than a weak one.
        probability += request.SectorQuadrant switch
        {
            SectorQuadrant.Leading => 0.02m,
            SectorQuadrant.Improving => 0.01m,
            SectorQuadrant.Weakening => -0.01m,
            SectorQuadrant.Lagging => -0.025m,
            _ => 0m
        };

        // Accumulation footprint: volume concentrating on advancing sessions is the closest
        // public read on what a net-foreign-flow column would have shown.
        if (request.UpDownVolumeRatio20d is decimal upDownVolume)
        {
            probability += upDownVolume switch
            {
                >= 1.25m => 0.015m,
                < 0.70m => -0.015m,
                _ => 0m
            };
        }

        // Wide-tick friction: a round trip that crosses half a percent of spread costs real
        // probability on top of the modelled fees, so it is charged instead of absorbed.
        if (request.TickSpreadPct is decimal tickSpreadPct && tickSpreadPct >= 0.50m)
        {
            probability -= 0.01m;
        }

        // Confirmed foreign demand from the official PSE Daily Quotation Report. The ratio is
        // net foreign flow over 20-day turnover, so the tilt survives differences in name size.
        // A confirmed distribution print costs more than an equivalent accumulation print
        // pays, because a foreign seller can keep selling into strength.
        if (request.NetForeignTurnoverRatio is decimal foreignRatio)
        {
            probability += foreignRatio switch
            {
                >= QuantitativeFilterService.ForeignFlowStrongRatio => 0.02m,
                > 0m => 0.005m,
                <= -QuantitativeFilterService.ForeignFlowStrongRatio => -0.03m,
                < 0m => -0.01m,
                _ => 0m
            };
        }

        // Rate gravity: a cash yield that no longer clears the US 10-year is not income, it is
        // duration risk. The drag lands hardest on the income books and on the bond proxies
        // (REITs, utilities, telcos) whose valuation is a spread trade against Treasuries.
        if (request.TrailingDividendYieldPct is decimal cashYield &&
            request.Us10YClosePct is decimal us10Y &&
            us10Y > 0m)
        {
            var yieldSpread = cashYield - us10Y;
            var horizonWeight = request.Profile.Horizon is SignalHorizon.MONTH or SignalHorizon.YEAR
                ? 1m
                : 0.5m;
            var sectorWeight = IsBondProxySector(request.Sector) ? 1.5m : 1m;

            if (yieldSpread < 0m)
            {
                probability -= 0.02m * horizonWeight * sectorWeight;
            }
            else if (yieldSpread >= 2m)
            {
                probability += 0.01m * horizonWeight * sectorWeight;
            }
        }

        // Walk-forward calibration: the realized outcome of this desk's own past verdicts
        // versus the probability it claimed. Clamped, because a small sample must not be
        // allowed to rewrite the model.
        if (request.CalibrationOffsetPct is decimal calibrationOffset)
        {
            probability += Math.Clamp(calibrationOffset / 100m, -0.05m, 0.05m);
        }

        // The self-trained money-manager model: only a promoted model is ever present here,
        // and it may move the odds by at most six points from the prior it was fitted against.
        // The trainer fits only on non-bearish sessions, so applying a bull/neutral learned residual
        // in a BEARISH regime would extrapolate out of distribution.
        var learnedTilt = 0m;
        var applyLearnedModel = request.LearnedModel is not null &&
                                request.LearnedFeatures is not null &&
                                request.MarketRegime != MarketRegime.BEARISH;
        if (applyLearnedModel)
        {
            learnedTilt = request.LearnedModel!.ResidualTilt(probability, request.LearnedFeatures!);
            probability += learnedTilt;
        }

        var softWarnings = (request.TrapFlags ?? Array.Empty<PseTrapFlag>())
            .Where(flag => flag != PseTrapFlag.NONE)
            .Distinct()
            .Count();

        probability -= Math.Min(0.045m, softWarnings * 0.015m);

        // Conviction may tilt the odds by at most +/-4 points: it is a weak signal, not
        // the odds themselves.
        var convictionTilt = Math.Clamp((request.ConvictionScore - 50m) / 50m, -1m, 1m) * 0.04m;
        probability = decimal.Round(Math.Clamp(probability + convictionTilt, 0.05m, 0.95m), 4, MidpointRounding.AwayFromZero);

        var expectedValue = PseBoardLotService.ComputeExpectedValueR(probability, netRiskReward);
        var kelly = PseBoardLotService.ComputeQuarterKellyFraction(probability, netRiskReward);
        var expectedLossR = 1m;
        var expiryRate = 0m;

        // Payoff separation. The hit rate says *how often* the bet pays; it says nothing about
        // how much. An empirical sample that shows most winning windows expiring just above
        // entry cannot be valued at the full net reward, and a sample whose losing windows
        // scratch above the stop cannot be valued at a full -1R either. Both numbers come from
        // the ticker's own resolved history, so expected value is priced off what it delivered.
        if (request.BarrierStats is { Trials: > 0 } barrier)
        {
            var winPayoff = barrier.AverageWinR > 0m
                ? Math.Min(netRiskReward, barrier.AverageWinR)
                : netRiskReward;

            expectedLossR = barrier.AverageLossR > 0m ? barrier.AverageLossR : 1m;
            expiryRate = barrier.ExpiryRate;

            expectedValue = PseBoardLotService.ComputeExpectedValueR(probability, winPayoff, expectedLossR);
            kelly = PseBoardLotService.ComputeQuarterKellyFraction(
                probability,
                expectedLossR > 0m ? winPayoff / expectedLossR : winPayoff);
        }

        // A promoted model also owns how much of full Kelly the desk should stake. The
        // Quarter-Kelly figure is the neutral reference, so 0.25x leaves it unchanged.
        if (applyLearnedModel && request.LearnedModel is { } learnedModel && learnedModel.KellyScale > 0m)
        {
            kelly = decimal.Round(kelly * learnedModel.KellyScale, 6, MidpointRounding.AwayFromZero);
        }

        EdgeVerdict verdict;

        if (expectedValue <= 0m)
        {
            verdict = EdgeVerdict.WATCH_ONLY;
        }
        else if (netRiskReward >= request.Profile.TargetNetRiskReward &&
                 expectedValue >= request.Profile.MinimumExpectedValueR &&
                 probability >= breakEven + 0.05m &&
                 request.MarketRegime != MarketRegime.BEARISH &&
                 softWarnings == 0)
        {
            verdict = EdgeVerdict.TRADE;
        }
        else
        {
            verdict = EdgeVerdict.SMALL_SIZE;
        }

        return new EdgeAssessment(netRiskReward, probability, breakEven, expectedValue, kelly, verdict)
        {
            LearnedEdgeTilt = learnedTilt,
            ExpectedLossR = expectedLossR,
            ExpiryRate = expiryRate
        };
    }

    /// <summary>
    /// Sectors whose valuation is a spread trade against government yields. A rate move hits
    /// them through the discount rate as well as through the dividend comparison.
    /// </summary>
    public static bool IsBondProxySector(string? sector) =>
        !string.IsNullOrWhiteSpace(sector) &&
        (sector.Contains("REIT", StringComparison.OrdinalIgnoreCase) ||
         sector.Contains("Utilities", StringComparison.OrdinalIgnoreCase) ||
         sector.Contains("Telecom", StringComparison.OrdinalIgnoreCase) ||
         sector.Contains("Property", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Recommended portfolio cash reserve for a run. Weak breadth, a bearish tape and a
    /// short list of positive-EV setups all argue for holding more cash instead of
    /// forcing ten bets; the result is a whole percentage in <c>[0, 100]</c>.
    /// <paramref name="netForeignFlowPhp"/> is the official session market flow when the PSE
    /// Daily Quotation Report is available. A heavy foreign distribution day (at or below
    /// -PHP 500M) adds 5 points of cash; an unreported flow changes nothing.
    /// </summary>
    public static decimal ComputeRecommendedCashPct(
        MarketRegime regime,
        decimal? breadthAboveSma50Pct,
        int tradeCount,
        int smallSizeCount,
        decimal? netForeignFlowPhp = null)
    {
        decimal cash = regime switch
        {
            MarketRegime.BEARISH => 70m,
            MarketRegime.NEUTRAL => 40m,
            _ => 20m
        };

        if (breadthAboveSma50Pct is decimal breadth)
        {
            if (breadth < 30m)
            {
                cash += 20m;
            }
            else if (breadth < 45m)
            {
                cash += 10m;
            }
            else if (breadth > 65m)
            {
                cash -= 10m;
            }
        }

        // Each unsold slot (TRADE counts double a half-sized SMALL_SIZE) rolls into cash.
        var actionable = Math.Max(0, tradeCount) + (Math.Max(0, smallSizeCount) * 0.5m);
        if (actionable < 10m)
        {
            cash += (10m - actionable) * 4m;
        }

        // Official foreign distribution: when the market is a net seller of PHP 500M or more,
        // the desk holds one extra slice of cash instead of buying into the supply.
        if (netForeignFlowPhp is decimal marketFlow && marketFlow <= MarketOutflowCashThresholdPhp)
        {
            cash += 5m;
        }

        return decimal.Round(Math.Clamp(cash, 0m, 100m), 0, MidpointRounding.AwayFromZero);
    }
}
