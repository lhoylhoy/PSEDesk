using System.Globalization;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Entities;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Turns the stored session plus today's run into one calm, deterministic market call for
/// regular buyers. Five pillars tilt a neutral 50: the PSEi's own trend (up to 20 points),
/// market breadth (14), sector rotation (8), reported foreign flow (6) and the volatility /
/// peso backdrop (6). A BEARISH regime always forces cash; a "ride the wave" call with no
/// actionable pick steps down to "be selective". Nothing here reads the model's prose and
/// nothing is random, so the same session always produces the same traffic light.
/// </summary>
public static class MarketDirectionService
{
    public const string RideTheWave = "RIDE_THE_WAVE";
    public const string BeSelective = "BE_SELECTIVE";
    public const string StayInCash = "STAY_IN_CASH";

    public const string UpTrend = "UPTREND";
    public const string Sideways = "SIDEWAYS";
    public const string DownTrend = "DOWNTREND";

    public const string HighConfidence = "HIGH";
    public const string ModerateConfidence = "MODERATE";
    public const string CautiousConfidence = "CAUTIOUS";

    private const int NeutralScore = 50;
    private const int MinScore = 10;
    private const int MaxScore = 90;

    // Calls are read off the score with a buffer, so a 51 does not pretend to be a trend.
    private const int RideTheWaveScore = 60;
    private const int BeSelectiveScore = 45;

    public static MarketDirectionForecastDto Evaluate(
        DailyMarketSnapshot snapshot,
        SignalRun? run = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var rotation = MarketDataMapper.DeserializeSectorRotation(snapshot.SectorRotationJson);
        var pillars = new List<int>(5);
        var reasons = new List<string>(5);

        var indexTilt = ScoreIndexTrend(snapshot, reasons, out var indexReported);
        if (indexReported)
        {
            pillars.Add(indexTilt);
        }
        else
        {
            reasons.Add("The PSEi averages were not reported for this session, so the trend pillars could not vote.");
        }

        var breadthTilt = ScoreBreadth(snapshot, reasons, out var breadthReported);
        if (breadthReported)
        {
            pillars.Add(breadthTilt);
        }

        var rotationTilt = ScoreRotation(rotation, reasons, out var rotationReported);
        if (rotationReported)
        {
            pillars.Add(rotationTilt);
        }

        var foreignTilt = ScoreForeignFlow(snapshot, reasons);
        if (snapshot.NetForeignFlowPhp.HasValue)
        {
            pillars.Add(foreignTilt);
        }

        var backdropTilt = ScoreVolatilityAndPeso(snapshot, reasons, out var backdropReported);
        if (backdropReported)
        {
            pillars.Add(backdropTilt);
        }

        var score = Math.Clamp(NeutralScore + pillars.Sum(), MinScore, MaxScore);
        var direction = score >= RideTheWaveScore ? UpTrend : score <= 40 ? DownTrend : Sideways;

        var actionableCount = run?.Items.Count(
            item => item.EdgeVerdict is EdgeVerdict.TRADE or EdgeVerdict.SMALL_SIZE) ?? 0;
        var hasRun = run is not null;

        var call = snapshot.MarketRegime == MarketRegime.BEARISH
            ? StayInCash
            : score >= RideTheWaveScore
                ? RideTheWave
                : score >= BeSelectiveScore
                    ? BeSelective
                    : StayInCash;

        // A green tape with an empty list is a trap for a beginner: the desk never tells
        // them to ride a wave it cannot fill with a single fee-paid setup.
        var steppedDown = call == RideTheWave && hasRun && actionableCount == 0;
        if (steppedDown)
        {
            call = BeSelective;
            reasons.Insert(Math.Min(1, reasons.Count),
                "No pick cleared its fees today, so the desk is waiting instead of chasing the bounce.");
        }

        if (snapshot.MarketRegime == MarketRegime.BEARISH)
        {
            reasons.Insert(Math.Min(1, reasons.Count),
                "The desk's regime read is bearish, and cash is the only position that cannot fall.");
        }

        var confidence = GradeConfidence(score, pillars);
        var (headline, actionSummary) = BuildCopy(
            call,
            direction,
            steppedDown,
            run,
            actionableCount,
            snapshot.MarketRegime == MarketRegime.BEARISH);

        if (reasons.Count < 2)
        {
            reasons.Add(
                "Several dials were unreported for this session, so the call leans on the PSEi's own trend and stays cautious.");
        }

        return new MarketDirectionForecastDto(
            call,
            direction,
            score,
            confidence,
            headline,
            actionSummary,
            reasons.Take(4).ToList(),
            LeadingSectors(rotation),
            LaggingSectors(rotation));
    }

    /// <summary>
    /// Keeps a stored scorecard honest when it is shown next to a beginner: zero resolved
    /// picks can never look like a 0% win rate, and the "calibrated" badge only appears once
    /// at least twenty picks have been graded.
    /// </summary>
    public static TrustPulseDto ToTrustPulse(SignalScorecardDto scorecard) => new(
        scorecard.Horizon,
        scorecard.TotalPicks,
        scorecard.ResolvedCount,
        scorecard.OpenCount,
        scorecard.WinRate,
        scorecard.ExpectancyR,
        scorecard.ProfitFactor,
        IsCalibrated: scorecard.ResolvedCount >= 20);
    private static int ScoreIndexTrend(
        DailyMarketSnapshot snapshot,
        List<string> reasons,
        out bool reported)
    {
        var tilt = 0;
        var hasSma50 = snapshot.PseiSma50 > 0m;
        var hasSma200 = snapshot.PseiSma200 > 0m;

        if (hasSma50)
        {
            tilt += snapshot.PseiClose >= snapshot.PseiSma50 ? 8 : -8;
        }

        if (hasSma200)
        {
            tilt += snapshot.PseiClose >= snapshot.PseiSma200 ? 7 : -7;
        }

        if (hasSma50 && hasSma200)
        {
            tilt += snapshot.PseiSma50 >= snapshot.PseiSma200 ? 5 : -5;
        }

        reported = hasSma50 || hasSma200;
        if (!reported)
        {
            return 0;
        }

        var position50 = hasSma50
            ? (snapshot.PseiClose >= snapshot.PseiSma50 ? "above" : "below")
            : "unreported against";
        var position200 = hasSma200
            ? (snapshot.PseiClose >= snapshot.PseiSma200 ? "above" : "below")
            : "unreported against";
        var alignment = hasSma50 && hasSma200
            ? snapshot.PseiSma50 >= snapshot.PseiSma200
                ? " and the 50-day sits above the 200-day"
                : " and the 50-day sits below the 200-day"
            : string.Empty;

        reasons.Add(
            $"The PSEi closed at {Format(snapshot.PseiClose)} — {position50} its 50-day average, " +
            $"{position200} its 200-day{alignment}.");

        return tilt;
    }

    private static int ScoreBreadth(
        DailyMarketSnapshot snapshot,
        List<string> reasons,
        out bool reported)
    {
        var tilt = 0;
        reported = false;

        if (snapshot.BreadthAboveSma50Pct is decimal breadth)
        {
            reported = true;
            tilt += breadth >= 60m ? 10
                : breadth >= 50m ? 5
                : breadth >= 40m ? -3
                : -8;
        }

        if (snapshot.Stage2Count is int stage2 &&
            snapshot.EvaluatedStockCount is int evaluated &&
            evaluated > 0)
        {
            reported = true;
            var stage2Share = (decimal)stage2 / evaluated;
            tilt += stage2Share >= 0.35m ? 4
                : stage2Share >= 0.20m ? 2
                : stage2Share >= 0.10m ? -2
                : -4;
        }

        if (!reported)
        {
            return 0;
        }

        if (snapshot.BreadthAboveSma50Pct is decimal breadthPct)
        {
            var stageText = snapshot.Stage2Count is int stage && snapshot.EvaluatedStockCount is int count
                ? $" and {stage} of the {count} stocks checked are in a confirmed uptrend"
                : string.Empty;

            reasons.Add(
                $"{Format(breadthPct)}% of the stocks we track are above their 50-day average{stageText}.");
        }
        else
        {
            reasons.Add(
                $"{snapshot.Stage2Count} of the {snapshot.EvaluatedStockCount} stocks we check are in a confirmed uptrend.");
        }

        return tilt;
    }

    private static int ScoreRotation(
        IReadOnlyList<SectorRotationDto> rotation,
        List<string> reasons,
        out bool reported)
    {
        reported = rotation.Count > 0;
        if (!reported)
        {
            return 0;
        }

        var improving = rotation.Count(
            read => read.Quadrant is SectorQuadrant.Leading or SectorQuadrant.Improving);
        var weakening = rotation.Count(
            read => read.Quadrant is SectorQuadrant.Weakening or SectorQuadrant.Lagging);
        var ratio = (decimal)(improving - weakening) / rotation.Count;
        var tilt = (int)Math.Round(ratio * 8m, 0, MidpointRounding.AwayFromZero);

        var leaders = LeadingSectors(rotation);
        var laggards = LaggingSectors(rotation);
        var pieces = new List<string>(2);

        if (leaders.Count > 0)
        {
            pieces.Add($"money is moving into {JoinNames(leaders.Take(2))}");
        }

        if (laggards.Count > 0)
        {
            pieces.Add($"{JoinNames(laggards.Take(2))} {(laggards.Count == 1 ? "is" : "are")} lagging");
        }

        if (pieces.Count > 0)
        {
            reasons.Add($"Sector rotation is mixed: {string.Join(", while ", pieces)}.");
        }

        return tilt;
    }

    private static int ScoreForeignFlow(DailyMarketSnapshot snapshot, List<string> reasons)
    {
        if (snapshot.NetForeignFlowPhp is not decimal flow)
        {
            return 0;
        }

        var tilt = flow >= 250_000_000m ? 6
            : flow >= 50_000_000m ? 3
            : flow <= -500_000_000m ? -6
            : flow <= -100_000_000m ? -3
            : 0;

        var verb = flow >= 0m ? "bought" : "sold";
        reasons.Add(
            $"Foreign investors {verb} a net {FormatPeso(Math.Abs(flow))} of PSE shares in the official daily report.");

        return tilt;
    }

    private static int ScoreVolatilityAndPeso(
        DailyMarketSnapshot snapshot,
        List<string> reasons,
        out bool reported)
    {
        var tilt = 0;
        reported = false;
        var notes = new List<string>(2);

        if (snapshot.PseiRealizedVol20dPct is decimal volatility)
        {
            reported = true;
            tilt += volatility >= 25m ? -3 : volatility >= 18m ? -1 : volatility <= 12m ? 2 : 0;

            if (volatility >= 18m)
            {
                notes.Add($"the index has been jumpy ({Format(volatility)}% 20-day volatility)");
            }
        }

        if (snapshot.UsdPhpChange20dPct is decimal pesoChange)
        {
            reported = true;
            tilt += pesoChange >= 1.5m ? -3 : pesoChange >= 0.75m ? -1 : pesoChange <= -1m ? 2 : 0;

            if (pesoChange >= 0.75m)
            {
                notes.Add($"the peso has weakened {Format(pesoChange)}% in 20 sessions");
            }
        }

        if (notes.Count > 0)
        {
            reasons.Add($"The backdrop is a headwind: {string.Join(" and ", notes)}.");
        }

        return Math.Clamp(tilt, -6, 6);
    }

    private static string GradeConfidence(int score, IReadOnlyList<int> pillars)
    {
        var tilt = score - NeutralScore;
        if (tilt == 0 || pillars.Count == 0)
        {
            return CautiousConfidence;
        }

        var sign = Math.Sign(tilt);
        var aligned = pillars.Count(pillar => pillar != 0 && Math.Sign(pillar) == sign);

        if (pillars.Count >= 4 && aligned >= 3 && Math.Abs(tilt) >= 8)
        {
            return HighConfidence;
        }

        if (pillars.Count >= 3 && aligned >= 2)
        {
            return ModerateConfidence;
        }

        return CautiousConfidence;
    }

    private static (string Headline, string ActionSummary) BuildCopy(
        string call,
        string direction,
        bool steppedDown,
        SignalRun? run,
        int actionableCount,
        bool bearish)
    {
        var cashPct = run is { RecommendedCashPct: > 0m }
            ? (int)Math.Round(run.RecommendedCashPct, MidpointRounding.AwayFromZero)
            : call switch
            {
                RideTheWave => 30,
                BeSelective => 55,
                _ => 80
            };

        // Descriptive model posture only: the sentence reports what the desk's own sizing did,
        // it never instructs the reader to place an order.
        var picksText = run is null
            ? string.Empty
            : actionableCount == 0
                ? " No setup cleared its fee-adjusted edge test, so the list stays empty."
                : $" {actionableCount} setup{(actionableCount == 1 ? string.Empty : "s")} cleared the fee-adjusted edge test.";

        if (steppedDown)
        {
            return (
                "The tape is leaning up — but nothing cleared the hurdle",
                $"Model posture holds about {cashPct}% cash. Qualifying setups are scarce in this session.{picksText}");
        }

        return call switch
        {
            RideTheWave => (
                "Ride the wave — the market is leaning up",
                $"Model posture holds about {cashPct}% cash and focuses on the highest-ranked setups below.{picksText}"),
            BeSelective => (
                direction == DownTrend
                    ? "Choose very carefully — the market is leaning down"
                    : "Be selective — the market is not clearly trending",
                $"Model posture holds about {cashPct}% cash and accepts only setups that clear their fees.{picksText}"),
            _ => (
                "Defensive tape — model stands in cash",
                bearish
                    ? $"Model posture holds about {cashPct}% cash because fewer setups succeed in a bearish regime.{picksText}"
                    : $"Model posture holds about {cashPct}% cash because few setups in this tape clear their fees.{picksText}")
        };
    }

    private static List<string> LeadingSectors(IReadOnlyList<SectorRotationDto> rotation) =>
        rotation
            .Where(read => read.Quadrant is SectorQuadrant.Leading or SectorQuadrant.Improving)
            .OrderByDescending(read => read.CompositeScore)
            .ThenBy(read => read.Sector, StringComparer.Ordinal)
            .Select(read => read.Sector)
            .Take(3)
            .ToList();

    private static List<string> LaggingSectors(IReadOnlyList<SectorRotationDto> rotation) =>
        rotation
            .Where(read => read.Quadrant is SectorQuadrant.Weakening or SectorQuadrant.Lagging)
            .OrderBy(read => read.CompositeScore)
            .ThenBy(read => read.Sector, StringComparer.Ordinal)
            .Select(read => read.Sector)
            .Take(3)
            .ToList();

    private static string JoinNames(IEnumerable<string> names) => string.Join(" and ", names);

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatPeso(decimal value)
    {
        if (value >= 1_000_000_000m)
        {
            return $"PHP {Format(decimal.Round(value / 1_000_000_000m, 2, MidpointRounding.AwayFromZero))}B";
        }

        if (value >= 1_000_000m)
        {
            return $"PHP {Format(decimal.Round(value / 1_000_000m, 1, MidpointRounding.AwayFromZero))}M";
        }

        return $"PHP {value.ToString("N0", CultureInfo.InvariantCulture)}";
    }
}

