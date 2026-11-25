using System.Globalization;
using System.Text;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Infrastructure.AI;

/// <summary>
/// Builds the system/user/correction prompts. Kept as pure string composition so the
/// trading rules are easy to review and unit-test. Each horizon gets its own mission,
/// and every prompt is grounded in the retrieved knowledge-base sections.
/// </summary>
public static class OllamaPromptBuilder
{
    /// <summary>
    /// Backwards-compatible system prompt (the WEEK book) for callers that do not pass a
    /// horizon.
    /// </summary>
    public static string SystemPrompt => BuildSystemPrompt(SignalHorizon.WEEK);

    public static string BuildSystemPrompt(SignalHorizon horizon)
    {
        var profile = HorizonProfile.For(horizon);

        return $$"""
        You are the head of a Philippine equity desk managing a {{horizon}}-horizon long-only book on the Philippine Stock Exchange (PSE).

        HORIZON MISSION ({{horizon}}): {{profile.Mission}}

        You receive a pre-filtered candidate universe that has already passed a hard PHP 5,000,000 average 20-day turnover floor and the structural trap screen (rights offerings, follow-on offers, tender-offer pins, halt risk). Every candidate carries its PSE board lot, the +/- static price band around the reference close, its Weinstein stage and its 52-week high, plus its 63-session relative strength versus the PSEi, ATR14 as a share of price, a volatility-contraction ratio (below 0.85 means a tight base) and the ticker's own historical ATR-barrier hit rate. BREADTH reports participation across the evaluated universe: weak breadth means breakouts fail more often, so demand tighter risk rather than more conviction. You are a money manager, not a screener: weight where the money is going, not only how a single chart looks.

        INSTITUTIONAL DIALS you must weigh (all optional; "n/a" means unreported, never zero):
        - SECTOR ROTATION ranks PSE sectors LEADING / IMPROVING / WEAKENING / LAGGING. Prefer picks in leading and improving clusters. A pick in a lagging sector needs an exceptional, name-specific catalyst and a lower conviction.
        - ACCUMULATION: up/down volume ratio above 1.25 and a positive Chaikin Money Flow mean volume is building on advancing sessions; negative money flow with heavy volume is distribution, no matter how good the price pattern looks.
        - QUALITY: trend efficiency near 1.0 is a smooth institutional trend; low efficiency is chop that pays the round trip without going anywhere. High beta and a deep 52-week max drawdown mean the name's risk budget is already spent.
        - SLIPPAGE: the tick spread printed for each name is what crossing the PSE queue costs. A wide spread on a thin name erodes a small edge — require more reward-to-risk, not a lower target.
        - HIT RATE BY HORIZON: grade each pick against its own horizon's barrier hit rate, not another book's.
        - CROSS-ASSET: the US 10Y yield (with its 20-session change in bps), Brent crude and EPHE (the MSCI Philippines ETF) are the offshore risk dials. Rising yields and a falling EPHE argue for smaller, more selective exposure; a falling US 10Y with a rising EPHE supports risk-on sizing. Brent only matters for energy, transport and consumer names.
        - FOREIGN FLOW: net foreign flow is the offshore bid. Persistent positive flow with rising turnover is sponsorship; heavy outflow alongside weak breadth is de-risking. Its provenance tag (pse-dqr-live or pse-dqr-upload) says where the number came from, and "n/a" means it was never reported, so never read it as zero.
        - DESK TRACK RECORD & LEARNED EDGE: DESK_TRACK_RECORD_AND_LEARNED_EDGE is the desk grading itself. It states how the book's graded picks actually resolved, which sectors paid, the recent stop-outs, whether a learned model earned the right to price this horizon, and the factors it leans on. Respect the record: repeat the conditions that worked and avoid the pattern behind the stop-outs. It never overrides rules 1-4 — the server still owns every price level, size, probability and verdict.

        PLAYBOOK_GROUNDING sections are retrieved from the desk's knowledge base and are the only authority you may cite. The mandatory `pse_rules` sections (board lots, tick gaps, static band, fee drag, structural traps) apply at every horizon.

        HARD RULES - a response that breaks any rule is rejected:
        1. Only choose tickers that appear in CANDIDATES. Never invent a ticker.
        2. Never pick a ticker whose avg_turnover_20d_php is below 5,000,000 or that carries a hard-disqualifying trap flag (SRO_DILUTION, TENDER_OFFER_PIN, TRADING_HALT_RISK).
        3. Every trade must offer at least 1:2.0 reward-to-risk: target_price > entry_price > stop_loss > 0 and (target_price - entry_price) / (entry_price - stop_loss) >= 2.0. For DAY ideas this ratio must survive the 0.69% round-trip fee drag (0.295% on the buy leg, 0.395% on the sell leg), so the gross ratio needs to be comfortably above 2.0 on tight stops.
        4. Use realistic PSE prices. Entries sit near the latest close, stops below structure (or {{profile.AtrStopMultiplier}}x ATR14), targets at resistance or {{profile.AtrTargetMultiplier}}x ATR14. Respect the static band and the board lot shown for each candidate: a price off the tick grid or a target outside the band is rejected by the server.
        5. Be market-regime aware. BULLISH: favour breakouts and momentum leaders, but never chase RSI above 75. NEUTRAL: favour pullbacks to EMA20/SMA50 with defined risk. BEARISH: lower gross exposure, prefer defensives and tight stops, and cap conviction scores.
        6. Audit the PSE Edge disclosures and trap flags. Earnings beats, dividends, capex and index inclusion are supportive catalysts; rights offerings, placements, block sales, adverse guidance or governance issues are reasons to avoid a name. Ex-dividend drops are mechanical, not bearish.
        7. Day-over-day hysteresis ({{horizon}}): an incumbent from YESTERDAY_TOP_10 keeps its slot unless (a) a challenger's conviction is at least {{profile.HysteresisConvictionDelta}} points higher, (b) the incumbent's stop was invalidated by price, (c) its target was hit, or (d) a materially adverse disclosure was published.
        8. Rank 1..10 by conviction, highest first, with unique ranks and unique tickers.
        9. Conviction scores are integers 0-100 for the whole book (typically 55-92). Do not give every name the same score.
        10. Write `thesis` in clear, everyday English a non-technical investor can understand, at most 45 words: what the price is doing, why now, the Peso buy / target / cut-loss levels and the main risk. Never use raw indicator shorthand (RSI, EMA20, SMA50, VCP, CMF20, RS63, -1R) in the prose — describe what price and volume are actually doing instead. Keep `deviation_reason` to at most 30 words of everyday English comparing with yesterday, or start it with "New entrant".
        11. Cite grounding honestly: `cited_rules` lists up to four references copied exactly from the PLAYBOOK_GROUNDING headings. Omit a rule rather than paraphrasing it, and return an empty array when no section informed the thesis.
        12. Every pick needs a `bear_case`: the single most likely way it fails, in at most 30 words of everyday English naming the invalidation level in Peso (never "-1R" or quant shorthand). A pre-mortem that restates the thesis is rejected.
        13. You do not own the odds. The server computes the fee-adjusted break-even rate, the win probability, the expected value in R, the Quarter-Kelly stake and the TRADE / SMALL_SIZE / WATCH_ONLY verdict. Your job is the thesis, the levels and the pre-mortem; over-promising in prose gains nothing.
        14. Return ONE JSON object and nothing else. No markdown fences, no commentary.

        RESPONSE CONTRACT (snake_case keys exactly as shown):
        {
          "top_10": [
            {
              "rank": 1,
              "ticker": "ICT",
              "conviction_score": 84,
              "entry_price": 430.00,
              "stop_loss": 410.00,
              "target_price": 475.00,
              "thesis": "Setup, catalyst, levels and risk in one sentence.",
              "bear_case": "A close below 410 on volume breaks the base and caps the rebound at 448.",
              "deviation_reason": "Why the rank changed versus yesterday.",
              "drift_status": "HELD",
              "previous_rank": 1,
              "cited_rules": ["pse_rules#orderable-prices-tick-sizes-and-board-lots"]
            }
          ],
          "dropped_from_yesterday": [
            {
              "ticker": "SCC",
              "previous_rank": 4,
              "drop_category": "INVALIDATED",
              "reason": "Closed below the prior stop after a volume-backed breakdown."
            }
          ]
        }

        drop_category must be one of INVALIDATED, TARGET_HIT or DISPLACED.
        drift_status must be one of NEW, HELD, UPGRADED or DOWNGRADED. The server recalculates drift deterministically, so provide your best estimate.
        If there is no yesterday list, return an empty dropped_from_yesterday array.
        """;
    }

    public static string BuildUserPrompt(OllamaSignalRequest request)
    {
        var profile = HorizonProfile.For(request.Horizon);
        var builder = new StringBuilder();

        builder.AppendLine($"TRADING DATE: {request.TradeDate:yyyy-MM-dd}");
        builder.AppendLine($"HORIZON: {request.Horizon}");
        builder.AppendLine($"MARKET REGIME: {request.Regime}");

        var netForeignFlow = request.NetForeignFlowPhp is decimal marketFlow
            ? $"PHP {Whole(marketFlow)}"
            : "N/A (unreported)";

        if (!string.IsNullOrWhiteSpace(request.ForeignFlowSource))
        {
            netForeignFlow += $" ({request.ForeignFlowSource})";
        }

        builder.AppendLine(
            $"PSEi close: {Money(request.PseiClose)} | Net foreign flow: {netForeignFlow} | USD/PHP: {Money(request.UsdPhp)}");
        builder.AppendLine(
            $"CROSS-ASSET: US 10Y {Yield(request.Us10YClosePct)} (20d {Bps(request.Us10YChange20dBps)}) | " +
            $"Brent {Usd(request.BrentUsd)} (20d {SignedPctOrNa(request.BrentChange20dPct)}) | " +
            $"EPHE {Usd(request.EpheCloseUsd)} (20d {SignedPctOrNa(request.EpheChange20dPct)})");
        builder.AppendLine($"REGIME NOTE: {request.RegimeNote}");

        if (request.Breadth is { } breadth)
        {
            builder.AppendLine(
                $"BREADTH: {Pct(breadth.AboveSma50Pct)} of {breadth.EvaluatedStockCount} names above their 50D average | " +
                $"{Pct(breadth.AboveSma200Pct)} above their 200D | {breadth.Stage2Count} in a Stage 2 uptrend");
        }

        AppendSectorRotation(builder, request.SectorRotation);
        AppendDeskTrackRecord(builder, request.DeskMemory, request.LearnedModel);

        builder.AppendLine();

        AppendPlaybooks(builder, request.ReferencePlaybooks);

        if (!string.IsNullOrWhiteSpace(request.FastExtractionSummary))
        {
            builder.AppendLine("FAST_EXTRACTION_NOTE (from the screening model, verify against CANDIDATES):");
            builder.AppendLine(request.FastExtractionSummary.Trim());
            builder.AppendLine();
        }

        builder.AppendLine($"CANDIDATES ({request.Candidates.Count}, all above the PHP 5,000,000 average 20-day turnover floor):");

        var index = 0;
        foreach (var candidate in request.Candidates.OrderByDescending(c => c.QuantPreScore))
        {
            index++;

            // An unreported foreign-flow figure is omitted entirely: the model must not
            // read a zero (or worse, a guess) as a signal.
            var netForeign = candidate.NetForeignTodayPhp is decimal flow
                ? $" | net foreign PHP {Whole(flow)}"
                : string.Empty;

            builder.AppendLine(
                $"{index}. {candidate.Ticker} | {candidate.Sector} | close {Money(candidate.ClosePrice)} | " +
                $"chg {Signed(candidate.ChangePct)}% | turnover PHP {Whole(candidate.AvgTurnover20dPhp)} | " +
                $"vol {Money(candidate.VolumeRatioVs20d)}x | RSI {Money(candidate.Rsi14)} | " +
                $"EMA20 {Money(candidate.Ema20)} | SMA50 {Money(candidate.Sma50)} | SMA200 {Money(candidate.Sma200)} | " +
                $"ATR14 {Money(candidate.Atr14)} | support {Money(candidate.SupportLevel)} | " +
                $"resistance {Money(candidate.ResistanceLevel)} | quant score {candidate.QuantPreScore}{netForeign}");

            builder.AppendLine($"   structure: {BuildStructureLine(candidate)}");
            builder.AppendLine($"   probability inputs: {BuildProbabilityLine(candidate)}");

            if (candidate.TrapFlags is { Count: > 0 } traps)
            {
                builder.AppendLine($"   trap flags: {string.Join(", ", traps)}");
            }

            builder.AppendLine(
                candidate.Disclosures.Count == 0
                    ? "   disclosures: none"
                    : "   disclosures: " + string.Join(
                        "; ",
                        candidate.Disclosures.Select(d => $"{d.Date:yyyy-MM-dd} [{d.Category}] {d.Headline}")));
        }

        builder.AppendLine();
        builder.AppendLine(
            request.YesterdayTop10.Count == 0
                ? $"YESTERDAY_TOP_10: none (this is the first run for the {request.Horizon} horizon)."
                : "YESTERDAY_TOP_10:");

        foreach (var prior in request.YesterdayTop10.OrderBy(p => p.Rank))
        {
            builder.AppendLine(
                $"#{prior.Rank} {prior.Ticker} | conviction {prior.ConvictionScore} | entry {Money(prior.EntryPrice)} | " +
                $"stop {Money(prior.StopLoss)} | target {Money(prior.TargetPrice)} | {prior.Thesis}");
        }

        builder.AppendLine();
        builder.AppendLine(
            $"TASK: Build today's {request.Horizon} Top 10 for the PSE from CANDIDATES. {profile.Mission} " +
            "Every level must be orderable on the PSE tick grid, inside the static band for DAY ideas, and must clear " +
            "the reward-to-risk rule. Give every pick a bear_case of at most 30 words naming the invalidation level. " +
            "Reconcile every departure in dropped_from_yesterday with a category and a short " +
            "reason, and list the PLAYBOOK_GROUNDING references that actually informed each thesis in cited_rules. " +
            "Return the JSON object now.");

        return builder.ToString();
    }

    /// <summary>
    /// The evidence-backed inputs the model should weigh instead of pure narrative: relative
    /// strength versus the index, the volatility budget, the base tightness and the ticker's
    /// own historical hit rate for an ATR-based barrier. Missing values print as "n/a".
    /// </summary>
    private static string BuildProbabilityLine(SignalCandidateInput candidate)
    {
        var parts = new List<string>
        {
            candidate.RelativeStrength63d is decimal relativeStrength
                ? $"RS63 {Signed(relativeStrength)}pp vs PSEi"
                : "RS63 n/a",
            candidate.AtrPct is decimal atrPct
                ? $"ATR14 {Money(atrPct)}% of price"
                : "ATR14 n/a",
            candidate.VolatilityContractionRatio is decimal contraction
                ? $"VCP {Money(contraction)}x"
                : "VCP n/a",
            candidate.EmpiricalWinRate is decimal baseRate
                ? $"historical ATR-barrier hit rate {Money(baseRate * 100m)}%"
                : "historical hit rate n/a"
        };

        // Per-horizon hit rates: the barrier geometry differs per book, so a DAY pick is not
        // graded off the WEEK number.
        var perHorizon = new List<string>();
        if (candidate.EmpiricalWinRateDay is decimal dayRate)
        {
            perHorizon.Add($"DAY {Money(dayRate * 100m)}%");
        }

        if (candidate.EmpiricalWinRateMonth is decimal monthRate)
        {
            perHorizon.Add($"MONTH {Money(monthRate * 100m)}%");
        }

        if (candidate.EmpiricalWinRateYear is decimal yearRate)
        {
            perHorizon.Add($"YEAR {Money(yearRate * 100m)}%");
        }

        if (perHorizon.Count > 0)
        {
            parts.Add($"hit rate by horizon {string.Join(" / ", perHorizon)}");
        }

        if (candidate.UpDownVolumeRatio20d is decimal upDown)
        {
            parts.Add($"up/down volume {Money(upDown)}x");
        }

        if (candidate.ChaikinMoneyFlow20d is decimal moneyFlow)
        {
            parts.Add($"CMF20 {Signed(moneyFlow)}");
        }

        if (candidate.TrendEfficiency63d is decimal efficiency)
        {
            parts.Add($"trend efficiency {Money(efficiency)}");
        }

        if (candidate.Beta252d is decimal beta)
        {
            parts.Add($"beta vs PSEi {Money(beta)}");
        }

        if (candidate.MaxDrawdown252dPct is decimal maxDrawdown)
        {
            parts.Add($"52w max drawdown {Money(maxDrawdown)}%");
        }

        if (!string.IsNullOrWhiteSpace(candidate.SectorQuadrant))
        {
            parts.Add($"sector quadrant {candidate.SectorQuadrant}");
        }

        if (candidate.TickSpreadPct is decimal tickSpread)
        {
            parts.Add($"tick spread {Money(tickSpread)}% each way");
        }

        if (candidate.TrailingDividendYieldPct is decimal dividendYield)
        {
            var exDate = candidate.LastExDividendDate is DateOnly ex
                ? ex.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "unknown";
            parts.Add($"div yield {Money(dividendYield)}% (last ex {exDate})");
        }

        return string.Join(" | ", parts);
    }

    /// <summary>
    /// The sector dial, ranked by composite score. Printed once per run so the model can
    /// favour accumulation in leading/improving clusters and avoid fresh risk in laggards.
    /// Omitted entirely when no rotation was computed, rather than printed as an empty list.
    /// </summary>
    private static void AppendSectorRotation(
        StringBuilder builder,
        IReadOnlyList<SectorRotationDto>? rotation)
    {
        if (rotation is null || rotation.Count == 0)
        {
            return;
        }

        var entries = rotation.Select(entry =>
            $"{entry.Sector} {entry.Quadrant} (score {Money(entry.CompositeScore)}, " +
            $"{entry.StockCount} names, {Pct(entry.AboveSma50Pct)} above 50D)");

        builder.AppendLine($"SECTOR ROTATION (ranked): {string.Join(" | ", entries)}");
    }

    /// <summary>
    /// The desk grading itself. Prints the realized record of its own graded picks, the
    /// sectors that paid and did not, the latest stop-outs and the promoted model's status and
    /// top factors. Anything unreported is omitted rather than printed as a zero.
    /// </summary>
    private static void AppendDeskTrackRecord(
        StringBuilder builder,
        DeskMemorySummary? memory,
        TrainedModelSummaryDto? model)
    {
        if (memory is null && model is null)
        {
            return;
        }

        var parts = new List<string>();

        if (memory is { ResolvedCount: > 0 })
        {
            parts.Add(
                $"graded {memory.ResolvedCount} picks | win rate {Pct(memory.WinRate * 100m)} | " +
                $"expectancy {Signed(memory.ExpectancyR)}R | Brier {Money(memory.BrierScore)} | " +
                $"calibration bias {Signed(memory.CalibrationBiasPct)}pp");
        }

        if (memory is { BestSectors.Count: > 0 })
        {
            parts.Add(
                "paid: " + string.Join(", ", memory.BestSectors.Select(sector =>
                    $"{sector.Sector} {Signed(sector.AverageRealizedNetR)}R over {sector.Samples}")));
        }

        if (memory is { WorstSectors.Count: > 0 })
        {
            parts.Add(
                "bled: " + string.Join(", ", memory.WorstSectors.Select(sector =>
                    $"{sector.Sector} {Signed(sector.AverageRealizedNetR)}R over {sector.Samples}")));
        }

        if (model is not null)
        {
            var status = model.IsActive
                ? $"learned weights active (held-out Brier {Money(model.TrainedBrierScore)} vs " +
                  $"{Money(model.BaselineBrierScore)} neutral prior, {Money(model.OptimalKellyFractionMultiplier)}x Kelly)"
                : $"{model.Status} — the structural prior still prices this book";

            parts.Add($"model: {status}");

            if (model.Weights.Count > 0)
            {
                parts.Add(
                    "leaning on " + string.Join(", ", model.Weights.Take(4).Select(weight =>
                        $"{weight.Label} {weight.Direction.ToLowerInvariant()} {Money(weight.NormalizedImpactPct)}%")));
            }
        }

        if (parts.Count == 0)
        {
            return;
        }

        builder.AppendLine($"DESK_TRACK_RECORD_AND_LEARNED_EDGE: {string.Join(" | ", parts)}");

        if (memory is { RecentStopOuts.Count: > 0 })
        {
            builder.AppendLine(
                "RECENT_STOP_OUTS: " + string.Join(", ", memory.RecentStopOuts.Select(stopOut =>
                    $"{stopOut.Ticker} on {stopOut.RunDate:yyyy-MM-dd} (entry {Money(stopOut.EntryPrice)}, " +
                    $"stop {Money(stopOut.StopLoss)}, {Signed(stopOut.RealizedNetR)}R, {stopOut.BarsHeld} bars)")));
        }
    }

    private static string BuildStructureLine(SignalCandidateInput candidate)
    {
        var structure = new List<string> { $"board lot {candidate.BoardLot}" };

        if (candidate.StaticFloor > 0m && candidate.StaticCeiling > 0m)
        {
            structure.Add($"static band {Money(candidate.StaticFloor)}-{Money(candidate.StaticCeiling)}");
        }

        if (!string.IsNullOrWhiteSpace(candidate.WeinsteinStage))
        {
            structure.Add($"stage {candidate.WeinsteinStage}");
        }

        if (candidate.Sma150 is decimal sma150)
        {
            structure.Add($"SMA150 {Money(sma150)}");
        }

        if (candidate.Week52High is decimal week52High && week52High > 0m)
        {
            var drawdown = (week52High - candidate.ClosePrice) / week52High * 100m;
            structure.Add($"52w high {Money(week52High)} ({Money(drawdown)}% below)");
        }

        return string.Join(" | ", structure);
    }

    private static void AppendPlaybooks(StringBuilder builder, IReadOnlyList<KnowledgeSnippet>? playbooks)
    {
        if (playbooks is null || playbooks.Count == 0)
        {
            builder.AppendLine(
                "PLAYBOOK_GROUNDING: unavailable. Rely on the hard rules only and return an empty cited_rules array.");
            builder.AppendLine();
            return;
        }

        builder.AppendLine("PLAYBOOK_GROUNDING (cite these reference strings exactly in cited_rules):");

        var index = 0;
        foreach (var playbook in playbooks)
        {
            index++;

            builder.AppendLine(
                $"[{index}] {playbook.Reference} | {playbook.SectionTitle} | horizons {string.Join('/', playbook.Horizons)}");
            builder.AppendLine(Truncate(playbook.Content, 1200));
            builder.AppendLine();
        }
    }

    /// <summary>
    /// Prompt for the cheap pre-extraction model: one compact, factual summary of the
    /// disclosure and trap situation. It never ranks trades.
    /// </summary>
    public static string BuildFastExtractionPrompt(OllamaSignalRequest request)
    {
        var builder = new StringBuilder();

        builder.AppendLine($"PSE trading date {request.TradeDate:yyyy-MM-dd}, regime {request.Regime}.");
        builder.AppendLine("Summarise the structural situation for the screened candidates below.");

        foreach (var candidate in request.Candidates.OrderByDescending(c => c.QuantPreScore).Take(12))
        {
            var traps = candidate.TrapFlags is { Count: > 0 } flags ? string.Join(", ", flags) : "none";

            var disclosures = candidate.Disclosures.Count == 0
                ? "none"
                : string.Join("; ", candidate.Disclosures.Select(d => $"({d.Category}) {d.Headline}"));

            builder.AppendLine(
                $"- {candidate.Ticker} | {candidate.Sector} | chg {Signed(candidate.ChangePct)}% | " +
                $"vol {Money(candidate.VolumeRatioVs20d)}x | RSI {Money(candidate.Rsi14)} | " +
                $"stage {candidate.WeinsteinStage ?? "unknown"} | traps: {traps} | disclosures: {disclosures}");
        }

        builder.AppendLine();
        builder.AppendLine(
            "Return ONE JSON object: {\"summary\": \"at most 120 words naming the tickers with fresh catalysts, " +
            "the names whose only news is mechanical (ex-dividend, index rebalance) and the names to avoid\", " +
            "\"trap_flags\": [\"TICKER: FLAG\"]} and nothing else.");

        return builder.ToString();
    }

    public static string BuildCorrectionPrompt(IReadOnlyList<string> errors, OllamaSignalRequest request)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Your previous response failed server-side validation:");
        foreach (var error in errors)
        {
            builder.AppendLine($"- {error}");
        }

        builder.AppendLine();
        builder.AppendLine(
            "Fix exactly these problems and return the COMPLETE JSON object again. Reminders: only tickers from " +
            $"CANDIDATES ({string.Join(", ", request.Candidates.Select(c => c.Ticker))}); " +
            "target_price > entry_price > stop_loss; reward-to-risk at least 2.0; prices must sit on the PSE tick grid " +
            "and inside the static band for DAY ideas; drop_category must be INVALIDATED, TARGET_HIT or DISPLACED; " +
            "cited_rules may only contain PLAYBOOK_GROUNDING references; every pick needs a bear_case of at most " +
            "30 words; no markdown and no prose.");
        return builder.ToString();
    }

    private static string Truncate(string value, int maxLength) =>
        string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[..maxLength] + "...";

    private static string Money(decimal value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Whole(decimal value) =>
        value.ToString("0", CultureInfo.InvariantCulture);

    private static string Signed(decimal value) =>
        (value >= 0m ? "+" : string.Empty) + value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Pct(decimal? value) =>
        value is decimal pct ? $"{Money(pct)}%" : "n/a";

    /// <summary>US 10Y is quoted as a percent yield; an unreported print stays "n/a".</summary>
    private static string Yield(decimal? value) =>
        value is decimal yield ? $"{yield.ToString("0.000", CultureInfo.InvariantCulture)}%" : "n/a";

    /// <summary>Yield changes are reported in basis points so 20 sessions read at a glance.</summary>
    private static string Bps(decimal? value) =>
        value is decimal bps
            ? (bps >= 0m ? "+" : string.Empty) + bps.ToString("0.0", CultureInfo.InvariantCulture) + "bps"
            : "n/a";

    private static string Usd(decimal? value) =>
        value is decimal usd ? $"USD {usd.ToString("0.00", CultureInfo.InvariantCulture)}" : "n/a";

    private static string SignedPctOrNa(decimal? value) =>
        value is decimal signed ? $"{Signed(signed)}%" : "n/a";
}

