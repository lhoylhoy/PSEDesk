using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Snapshot of the desk's capital position used to size one new order: equity, free cash,
/// the per-trade risk budget, the single-position cap, how much portfolio heat is left and,
/// since the pod-shop layer, the drawdown, sector and volatility dials that scale risk.
/// Every appended field has a default that reproduces the pre-governor sizing exactly.
/// </summary>
public sealed record OrderTicketContext(
    decimal EquityPhp,
    decimal CashPhp,
    decimal MaxRiskPerTradePct,
    decimal SmallSizeRiskMultiplier,
    decimal MaxPositionPct,
    decimal OpenHeatPct,
    decimal MaxPortfolioHeatPct,
    decimal DrawdownPct = 0m,
    decimal SoftDrawdownThresholdPct = 5m,
    decimal HardDrawdownThresholdPct = 10m,
    decimal? RegimeVolPct = null,
    decimal TargetAnnualVolPct = 18m,
    decimal? SectorExposurePct = null,
    decimal MaxSectorExposurePct = 35m,
    int SectorPositionCount = 0,
    int MaxPositionsPerSector = 2,
    string? Sector = null,
    decimal? ExistingEntryPrice = null,
    decimal? AtrPct = null,
    // Pyramiding: a legal add is measured against the open position's own state.
    /// <summary>Unrealized result of the open same-ticker position, in units of its entry risk.</summary>
    decimal? ExistingUnrealizedR = null,
    /// <summary>True when the open position's live stop already sits at or above break-even.</summary>
    bool ExistingStopAtBreakEven = false,
    /// <summary>Legal adds already taken into the open position (0, 1 or 2).</summary>
    int ExistingPyramidCount = 0,
    /// <summary>All-in cost already committed to the open same-ticker position, in pesos.</summary>
    decimal? ExistingPositionCostPhp = null);

/// <summary>
/// Server-owned order sizing. Given the account context and a signal's levels, it returns
/// the whole board lots the desk's own risk budget funds, the all-in cash outlay, the
/// fee-paid peso risk and reward, the break-even stop tick and the pod-shop risk governor's
/// verdict (drawdown scaling, sector caps, the legal pyramid shape) plus the PSE transaction-cost
/// analysis (slippage, ADV participation, execution guidance). The browser renders these
/// numbers; it never re-derives a size or a fee.
/// </summary>
public static class OrderTicketCalculator
{
    /// <summary>Maximum share of a name's 20-day turnover one position may absorb.</summary>
    public const decimal MaxLiquidityFraction = 0.02m;

    /// <summary>
    /// Quarter-Kelly stake treated as a normal-sized bet. The edge multiplier is the Kelly
    /// stake divided by this reference, so a 2% Kelly trade gets 1.0x the base risk.
    /// </summary>
    public const decimal KellyReferenceFraction = 0.02m;

    /// <summary>Floor on any governor multiplier: a weak edge is halved, never deleted.</summary>
    public const decimal MinGovernorMultiplier = 0.50m;

    /// <summary>Ceiling on any governor multiplier: a strong edge cannot double the base risk.</summary>
    public const decimal MaxGovernorMultiplier = 1.50m;

    /// <summary>Risk unit of the first pyramid add: half a normal clip.</summary>
    public const decimal FirstPyramidRiskMultiplier = 0.50m;

    /// <summary>Risk unit of the second (and final) pyramid add: a quarter of a normal clip.</summary>
    public const decimal SecondPyramidRiskMultiplier = 0.25m;

    /// <summary>Legal pyramid adds a single position may take.</summary>
    public const int MaxPyramidAdds = 2;

    /// <summary>Unrealized R the open position must show before another clip may be added.</summary>
    public const decimal PyramidMinUnrealizedR = 1.0m;

    public static OrderTicketDto Build(
        OrderTicketContext context,
        decimal entryPrice,
        decimal stopLoss,
        decimal targetPrice,
        EdgeVerdict verdict,
        decimal avgTurnover20dPhp,
        EdgeAssessment? edge = null)
    {
        var warnings = new List<string>();
        var boardLot = PseBoardLotService.GetBoardLotSize(entryPrice);
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(entryPrice);
        var netRiskPerShare = PseBoardLotService.ComputeNetRiskPerShare(entryPrice, stopLoss);
        var netRewardPerShare = PseBoardLotService.ComputeNetRewardPerShare(entryPrice, targetPrice);

        // 1. Verdict: the edge engine's own sizing opinion.
        var verdictMultiplier = 1m;
        if (verdict == EdgeVerdict.SMALL_SIZE)
        {
            verdictMultiplier = Math.Clamp(context.SmallSizeRiskMultiplier, 0m, 1m);
            warnings.Add("SMALL_SIZE halves the per-trade risk.");
        }
        else if (verdict == EdgeVerdict.WATCH_ONLY)
        {
            verdictMultiplier = 0m;
            warnings.Add("WATCH_ONLY carries zero risk-sized shares; this ticket is a study, not an order.");
        }

        // 2. Edge: fund a quarter-Kelly stake of 2% at 1.0x, a 1% stake at 0.5x, a 3%+ stake
        // at 1.5x. The edge engine already saw the fees, so this never re-prices the trade.
        var edgeMultiplier = 1m;
        if (edge is not null && verdictMultiplier > 0m)
        {
            edgeMultiplier = GovernorMultiplier(edge.QuarterKellyFraction / KellyReferenceFraction);
        }

        // 3. Volatility regime: the same setup deserves a smaller clip on a 30%-vol tape than
        // on a 12%-vol one. Null regime volatility leaves the multiplier at 1.0x, and a calm
        // tape never scales risk above the book's own budget — volatility targeting only cuts.
        var volatilityMultiplier = 1m;
        if (context.RegimeVolPct is decimal regimeVol && regimeVol > 0m && context.TargetAnnualVolPct > 0m)
        {
            volatilityMultiplier = Math.Min(1m, GovernorMultiplier(context.TargetAnnualVolPct / regimeVol));

            if (volatilityMultiplier < 1m)
            {
                warnings.Add(
                    $"Realised PSEi volatility of {Format(regimeVol)}% is above the {Format(context.TargetAnnualVolPct)}% " +
                    $"target, so the per-trade risk is scaled to {Format(volatilityMultiplier)}x.");
            }
        }

        // 4. Drawdown governor: defensive between the soft and hard thresholds, halted at the
        // hard one. This is the pod-shop rule that stops a losing book from doubling down.
        var drawdownPct = Math.Max(0m, context.DrawdownPct);
        var softDrawdown = Math.Max(0m, context.SoftDrawdownThresholdPct);
        var hardDrawdown = Math.Max(softDrawdown, context.HardDrawdownThresholdPct);
        var drawdownMultiplier = 1m;
        var riskGovernor = RiskGovernorState.Normal;

        if (hardDrawdown > 0m && drawdownPct >= hardDrawdown)
        {
            drawdownMultiplier = 0m;
            riskGovernor = RiskGovernorState.Halted;
            warnings.Add(
                $"The book is {Format(drawdownPct)}% below its peak, past the {Format(hardDrawdown)}% hard limit: " +
                "new risk is halted until the drawdown recovers.");
        }
        else if (softDrawdown > 0m && drawdownPct >= softDrawdown)
        {
            var span = hardDrawdown - softDrawdown;
            drawdownMultiplier = span <= 0m
                ? MinGovernorMultiplier
                : GovernorMultiplier(1m - (((drawdownPct - softDrawdown) / span) * (1m - MinGovernorMultiplier)));
            riskGovernor = RiskGovernorState.Defensive;
            warnings.Add(
                $"The book is {Format(drawdownPct)}% below its peak (soft limit {Format(softDrawdown)}%), so the " +
                $"per-trade risk is scaled to {Format(drawdownMultiplier)}x.");
        }

        // 5. Pyramiding: the only legal shape for adding to an open winner. The entry must be
        // above the open position, the stop must already sit at break-even, the trade must be
        // at least +1R, and there are only two adds — each smaller than the last. Anything
        // else is the martingale shape the pod shop forbids, so the ticket is blocked.
        var pyramidMultiplier = 1m;
        string? pyramidBlockedReason = null;

        if (context.ExistingEntryPrice is decimal existingEntry)
        {
            if (entryPrice <= existingEntry)
            {
                pyramidBlockedReason =
                    $"No averaging down or flat re-entry: the open position is at PHP {Format(existingEntry)} and " +
                    $"this entry is PHP {Format(entryPrice)}, so the ticket is blocked.";
            }
            else if (!context.ExistingStopAtBreakEven)
            {
                pyramidBlockedReason =
                    "Pyramiding requires the open stop already raised to the break-even tick " +
                    $"(PHP {Format(breakEven)} or better) before another clip is added.";
            }
            else if ((context.ExistingUnrealizedR ?? 0m) < PyramidMinUnrealizedR)
            {
                pyramidBlockedReason =
                    $"Pyramiding requires at least +{Format(PyramidMinUnrealizedR)}R unrealized profit and the stop " +
                    $"already at break-even; the open position is at {Format(context.ExistingUnrealizedR ?? 0m)}R.";
            }
            else if (context.ExistingPyramidCount >= MaxPyramidAdds)
            {
                pyramidBlockedReason =
                    $"Maximum of {MaxPyramidAdds} pyramid adds reached: bank the winner instead of adding a third clip.";
            }
            else
            {
                pyramidMultiplier = context.ExistingPyramidCount <= 0
                    ? FirstPyramidRiskMultiplier
                    : SecondPyramidRiskMultiplier;

                warnings.Add(
                    $"Pyramid add {context.ExistingPyramidCount + 1} of {MaxPyramidAdds}: this clip is sized at " +
                    $"{Format(pyramidMultiplier)}x the normal risk unit because it adds to an open winner.");
            }
        }

        var riskFraction = Math.Clamp(context.MaxRiskPerTradePct, 0m, 100m) / 100m;
        riskFraction *= verdictMultiplier * edgeMultiplier * volatilityMultiplier * drawdownMultiplier * pyramidMultiplier;

        var heatRoomPct = Math.Max(0m, context.MaxPortfolioHeatPct - Math.Max(0m, context.OpenHeatPct));
        var heatBudgetPhp = context.EquityPhp * heatRoomPct / 100m;
        var riskBudgetPhp = context.EquityPhp * riskFraction;

        if (heatBudgetPhp < riskBudgetPhp)
        {
            warnings.Add(
                $"Portfolio heat is at {Format(context.OpenHeatPct)}% of the {Format(context.MaxPortfolioHeatPct)}% cap, " +
                $"so the per-trade budget is cut to PHP {Format(heatBudgetPhp)}.");
        }

        var budgetPhp = decimal.Round(Math.Max(0m, Math.Min(riskBudgetPhp, heatBudgetPhp)), 2, MidpointRounding.AwayFromZero);
        var effectiveRiskPct = decimal.Round(riskFraction * 100m, 2, MidpointRounding.AwayFromZero);

        if (boardLot <= 0 || entryPrice <= 0m || netRiskPerShare <= 0m || budgetPhp <= 0m)
        {
            if (budgetPhp <= 0m && verdict != EdgeVerdict.WATCH_ONLY)
            {
                warnings.Add("The risk budget is exhausted: no shares can be sized.");
            }

            return EmptyTicket(
                boardLot,
                breakEven,
                budgetPhp,
                false,
                warnings,
                effectiveRiskPct,
                riskGovernor,
                drawdownPct,
                edgeMultiplier,
                volatilityMultiplier,
                drawdownMultiplier);
        }

        var buyCostPerShare = entryPrice * (1m + PseBoardLotService.BuyLegFeeRate);

        var riskShares = (long)Math.Floor(budgetPhp / netRiskPerShare);

        // The single-position cap applies to the *whole* position, so an add only gets the room
        // that is left above the cost already committed to the same ticker.
        var positionCapPhp = context.EquityPhp > 0m && context.MaxPositionPct > 0m
            ? context.EquityPhp * Math.Clamp(context.MaxPositionPct, 0m, 100m) / 100m
            : 0m;
        var committedPhp = Math.Max(0m, context.ExistingPositionCostPhp ?? 0m);

        var notionalShares = positionCapPhp > 0m
            ? (long)Math.Floor(Math.Max(0m, positionCapPhp - committedPhp) / buyCostPerShare)
            : long.MaxValue;
        var cashShares = context.CashPhp > 0m
            ? (long)Math.Floor(context.CashPhp / buyCostPerShare)
            : 0L;

        var hasLiquidityCap = avgTurnover20dPhp > 0m;
        var liquidityShares = hasLiquidityCap
            ? (long)Math.Floor(avgTurnover20dPhp * MaxLiquidityFraction / entryPrice)
            : long.MaxValue;

        // 6. Pod-shop position rules: the pyramid shape above, never exceed the per-sector name
        // cap (a same-ticker add is the same name, so it is exempt from the *count*), and never
        // push a sector past its exposure ceiling.
        if (pyramidBlockedReason is not null)
        {
            warnings.Add(pyramidBlockedReason);

            return EmptyTicket(
                boardLot,
                breakEven,
                budgetPhp,
                false,
                warnings,
                effectiveRiskPct,
                RiskGovernorState.Blocked,
                drawdownPct,
                edgeMultiplier,
                volatilityMultiplier,
                drawdownMultiplier);
        }

        var sectorShares = long.MaxValue;

        if (context.ExistingEntryPrice is null &&
            context.MaxPositionsPerSector > 0 &&
            context.SectorPositionCount >= context.MaxPositionsPerSector)
        {
            warnings.Add(
                $"Pod rule: {context.MaxPositionsPerSector} {context.Sector ?? "sector"} positions are already open, " +
                "so no new name is sized in this sector.");

            return EmptyTicket(
                boardLot,
                breakEven,
                budgetPhp,
                false,
                warnings,
                effectiveRiskPct,
                RiskGovernorState.Blocked,
                drawdownPct,
                edgeMultiplier,
                volatilityMultiplier,
                drawdownMultiplier);
        }

        if (context.SectorExposurePct is decimal sectorExposure && context.MaxSectorExposurePct > 0m)
        {
            var roomPct = Math.Max(0m, context.MaxSectorExposurePct - Math.Max(0m, sectorExposure));
            var roomPhp = context.EquityPhp * roomPct / 100m;
            sectorShares = buyCostPerShare > 0m ? (long)Math.Floor(roomPhp / buyCostPerShare) : 0L;

            if (roomPct <= 0m)
            {
                warnings.Add(
                    $"The {Format(context.MaxSectorExposurePct)}% sector cap is already fully used by " +
                    $"{context.Sector ?? "this sector"}, so no new exposure can be added.");
            }
            else if (sectorShares < riskShares)
            {
                warnings.Add(
                    $"The {Format(context.MaxSectorExposurePct)}% sector cap leaves PHP {Format(roomPhp)} of room " +
                    "and caps the order.");
            }
        }

        var bindingShares = Math.Min(
            riskShares,
            Math.Min(notionalShares, Math.Min(cashShares, Math.Min(liquidityShares, sectorShares))));
        var liquidityCapped = hasLiquidityCap &&
                              liquidityShares < riskShares &&
                              liquidityShares < notionalShares &&
                              liquidityShares < cashShares;

        if (sectorShares < boardLot && sectorShares < riskShares)
        {
            return EmptyTicket(
                boardLot,
                breakEven,
                budgetPhp,
                false,
                warnings,
                effectiveRiskPct,
                RiskGovernorState.Blocked,
                drawdownPct,
                edgeMultiplier,
                volatilityMultiplier,
                drawdownMultiplier);
        }

        if (cashShares < riskShares && cashShares < notionalShares && cashShares < boardLot)
        {
            warnings.Add($"Free cash of PHP {Format(context.CashPhp)} cannot fund a board lot at PHP {Format(entryPrice)}.");
        }
        else if (cashShares < riskShares && cashShares <= notionalShares)
        {
            warnings.Add($"Free cash of PHP {Format(context.CashPhp)} caps the order.");
        }

        if (notionalShares < riskShares && notionalShares <= cashShares)
        {
            warnings.Add($"The {Format(context.MaxPositionPct)}% single-position cap caps the order.");
        }

        if (liquidityCapped)
        {
            warnings.Add(
                $"Capped at {Format(MaxLiquidityFraction * 100m)}% of the PHP {Format(avgTurnover20dPhp)} 20-day turnover.");
        }

        if (bindingShares < boardLot)
        {
            var lotCostPhp = decimal.Round(boardLot * buyCostPerShare, 2, MidpointRounding.AwayFromZero);
            warnings.Add(
                $"Equity of PHP {Format(context.EquityPhp)} cannot fund the {boardLot}-share board lot " +
                $"(about PHP {Format(lotCostPhp)} all-in).");

            return EmptyTicket(
                boardLot,
                breakEven,
                budgetPhp,
                liquidityCapped,
                warnings,
                effectiveRiskPct,
                riskGovernor,
                drawdownPct,
                edgeMultiplier,
                volatilityMultiplier,
                drawdownMultiplier);
        }

        var lots = bindingShares / boardLot;
        var shares = (int)Math.Min(lots * boardLot, int.MaxValue);

        // 6. PSE transaction-cost analysis: what crossing the spread and paying for size will
        // actually cost on this clip, in percent, in pesos, and how to work the order.
        var outlayPhp = PseBoardLotService.ComputeBuyTotalCostPhp(shares, entryPrice);
        var slippageRate = PseBoardLotService.ComputeEstimatedSlippageRate(
            entryPrice,
            outlayPhp,
            avgTurnover20dPhp,
            context.AtrPct);
        var slippagePhp = decimal.Round(outlayPhp * slippageRate, 2, MidpointRounding.AwayFromZero);
        var tickSpreadPct = decimal.Round(
            PseBoardLotService.ComputeTickSpreadRate(entryPrice) * 100m,
            2,
            MidpointRounding.AwayFromZero);

        var netRiskPhp = decimal.Round(shares * netRiskPerShare, 2, MidpointRounding.AwayFromZero);
        var netRewardPhp = decimal.Round(shares * netRewardPerShare, 2, MidpointRounding.AwayFromZero);
        var participation = PseBoardLotService.ComputeAdvParticipationRate(outlayPhp, avgTurnover20dPhp) ?? 0m;
        var participationPct = decimal.Round(participation * 100m, 2, MidpointRounding.AwayFromZero);

        if (slippageRate >= WideSlippageRate)
        {
            warnings.Add(
                $"Estimated round-trip slippage of {Format(slippageRate * 100m)}% (PHP {Format(slippagePhp)}) eats " +
                $"into the plan; the tick spread alone is {Format(tickSpreadPct)}%.");
        }

        if (participationPct >= 5m)
        {
            warnings.Add(
                $"This clip is {Format(participationPct)}% of the 20-day turnover: work it over several sessions " +
                "instead of crossing the book at once.");
        }

        return new OrderTicketDto(
            boardLot,
            shares,
            (int)lots,
            outlayPhp,
            netRiskPhp,
            netRewardPhp,
            breakEven,
            budgetPhp,
            liquidityCapped,
            warnings,
            effectiveRiskPct,
            riskGovernor,
            drawdownPct,
            edgeMultiplier,
            volatilityMultiplier,
            drawdownMultiplier,
            decimal.Round(slippageRate * 100m, 4, MidpointRounding.AwayFromZero),
            slippagePhp,
            decimal.Round(netRiskPhp + slippagePhp, 2, MidpointRounding.AwayFromZero),
            decimal.Round(Math.Max(0m, netRewardPhp - slippagePhp), 2, MidpointRounding.AwayFromZero),
            participationPct,
            ResolveExecutionGuidance(slippageRate, participationPct, liquidityCapped));
    }

    /// <summary>Slippage rate at which the ticket starts warning about transaction costs.</summary>
    public const decimal WideSlippageRate = 0.01m;

    private static OrderTicketDto EmptyTicket(
        int boardLot,
        decimal breakEven,
        decimal budgetPhp,
        bool liquidityCapped,
        IReadOnlyList<string> warnings,
        decimal effectiveRiskPct,
        string riskGovernor,
        decimal drawdownPct,
        decimal edgeMultiplier,
        decimal volatilityMultiplier,
        decimal drawdownMultiplier) =>
        new(
            boardLot,
            0,
            0,
            0m,
            0m,
            0m,
            breakEven,
            budgetPhp,
            liquidityCapped,
            warnings,
            effectiveRiskPct,
            riskGovernor,
            drawdownPct,
            edgeMultiplier,
            volatilityMultiplier,
            drawdownMultiplier,
            0m,
            0m,
            0m,
            0m,
            0m,
            ExecutionGuidance.NoTrade);

    /// <summary>
    /// Clamps and rounds a governor multiplier. Rounding to two decimals keeps the published
    /// size reproducible: equity * 1.0% * 0.85 always lands on the same board lot.
    /// </summary>
    private static decimal GovernorMultiplier(decimal raw) =>
        decimal.Round(
            Math.Clamp(raw, MinGovernorMultiplier, MaxGovernorMultiplier),
            2,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// How to work the order. A clip that is cheap to cross is marketable; a wide spread is
    /// limit-only; size that dwarfs the tape is staged over sessions.
    /// </summary>
    private static string ResolveExecutionGuidance(
        decimal slippageRate,
        decimal participationPct,
        bool liquidityCapped)
    {
        if (participationPct >= 5m)
        {
            return ExecutionGuidance.WorkTheOrder;
        }

        if (slippageRate >= WideSlippageRate)
        {
            return ExecutionGuidance.LimitOnly;
        }

        if (liquidityCapped || participationPct >= 2m)
        {
            return ExecutionGuidance.StagedEntry;
        }

        return ExecutionGuidance.LiquidMarketable;
    }

    private static string Format(decimal value) =>
        value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Pod-shop governor states reported on every ticket.</summary>
public static class RiskGovernorState
{
    public const string Normal = "NORMAL";
    public const string Defensive = "DEFENSIVE";
    public const string Halted = "HALTED";
    public const string Blocked = "BLOCKED";
}

/// <summary>Execution guidance labels, so the API and the UI share one vocabulary.</summary>
public static class ExecutionGuidance
{
    public const string LiquidMarketable = "LIQUID_MARKETABLE";
    public const string StagedEntry = "STAGED_ENTRY";
    public const string LimitOnly = "LIMIT_ONLY";
    public const string WorkTheOrder = "WORK_THE_ORDER";
    public const string NoTrade = "NO_TRADE";
}
