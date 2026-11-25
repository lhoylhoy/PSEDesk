using System.Globalization;
using PseSignalDesk.Application.Models;
using PseSignalDesk.Domain.Enums;
using PseSignalDesk.Domain.Services;

namespace PseSignalDesk.Application.Services;

/// <summary>
/// Builds the ride-the-wave roadmap for one pick and the "I already own it" check for one
/// position. Every price is snapped to the PSE's own tick grid, every fee is the same
/// 0.295% buy leg + 0.395% sell leg fee the risk engine charges, and the two trigger legs are the classic
/// +1R break-even lock and +1.5R EMA20 trail. The calculator never invents a level: the
/// maximum chase price walks down from the target's 20% mark until the remaining net R:R
/// still clears the horizon's floor.
/// </summary>
public static class WaveTradePlanCalculator
{
    public const decimal MinNetRiskReward = 1.25m;
    public const decimal DayMinNetRiskReward = 2.0m;

    public const string ReadyNow = "READY_NOW";
    public const string NearEntry = "NEAR_ENTRY";
    public const string ExtendedWaitPullback = "EXTENDED_WAIT_PULLBACK";
    public const string BrokenBelowStop = "BROKEN_BELOW_STOP";
    public const string WaitWatchOnly = "WAIT_WATCH_ONLY";

    private const decimal MaxChaseFractionOfTarget = 0.20m;
    private const decimal BreakEvenTriggerR = 1.0m;
    private const decimal TrailTriggerR = 1.5m;

    /// <summary>
    /// Four-step exit roadmap for a buyable pick: buy here, cut there, lock break-even at
    /// +1R, then trail under the 20-day average at +1.5R.
    /// </summary>
    public static WaveTradePlanDto BuildPlan(
        decimal entryPrice,
        decimal stopLoss,
        decimal targetPrice,
        decimal currentPrice,
        decimal? ema20,
        EdgeVerdict edgeVerdict,
        SignalHorizon horizon)
    {
        if (entryPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(entryPrice), entryPrice, "Entry must be positive.");
        }

        var (entry, stop, target) = PseBoardLotService.NormalizeTradePlan(entryPrice, stopLoss, targetPrice);
        var maxChase = ComputeMaxChasePrice(entry, stop, target, currentPrice, horizon);
        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(entry);
        var lockTrigger = ComputeTriggerPrice(entry, stop, target, BreakEvenTriggerR);
        var trailTrigger = ComputeTriggerPrice(entry, stop, target, TrailTriggerR);

        var trailStop = ema20 is decimal ema && ema > 0m
            ? PseBoardLotService.FloorToTick(ema)
            : (decimal?)null;
        if (trailStop is decimal trail && (trail <= stop || trail >= target))
        {
            // A trail under the cut-loss protects nothing, and one above the target can
            // never be reached before the position is already sold.
            trailStop = null;
        }

        var status = edgeVerdict == EdgeVerdict.WATCH_ONLY || currentPrice <= 0m
            ? WaitWatchOnly
            : currentPrice < stop
                ? BrokenBelowStop
                : currentPrice > maxChase
                    ? ExtendedWaitPullback
                    : currentPrice >= entry
                        ? ReadyNow
                        : NearEntry;

        return new WaveTradePlanDto(
            entry,
            stop,
            breakEven,
            target,
            maxChase,
            lockTrigger,
            trailTrigger,
            trailStop,
            status,
            HorizonProfile.For(horizon).ForwardWindowBars);
    }

    /// <summary>
    /// The highest price a buyer may still pay and keep the plan worth taking: the closer of
    /// the target's 20% band and the intraday static ceiling, walked down tick by tick until
    /// the fee-paid reward-to-risk still clears the horizon's floor. Never above the ideal
    /// entry — an entry with no edge is not a chase.
    /// </summary>
    public static decimal ComputeMaxChasePrice(
        decimal entry,
        decimal stopLoss,
        decimal target,
        decimal currentPrice,
        SignalHorizon horizon)
    {
        var distance = target - entry;
        if (entry <= 0m || distance <= 0m)
        {
            return PseBoardLotService.RoundToValidTick(entry);
        }

        var cap = entry + (distance * MaxChaseFractionOfTarget);
        if (horizon == SignalHorizon.DAY && currentPrice > 0m)
        {
            var limits = PseBoardLotService.GetStaticPriceLimits(currentPrice);
            if (limits.StaticCeiling > 0m && limits.StaticCeiling < cap)
            {
                cap = limits.StaticCeiling;
            }
        }

        var minimumNet = horizon == SignalHorizon.DAY ? DayMinNetRiskReward : MinNetRiskReward;
        var candidate = PseBoardLotService.FloorToTick(cap);

        while (candidate > entry)
        {
            var netRiskReward = PseBoardLotService.ComputeNetRiskReward(candidate, stopLoss, target);
            if (netRiskReward >= minimumNet)
            {
                return candidate;
            }

            candidate = PseBoardLotService.FloorToTick(candidate - PseBoardLotService.GetTickSize(candidate));
        }

        return PseBoardLotService.RoundToValidTick(entry);
    }

    /// <summary>
    /// The PSE tick at which the fee-paid open gain equals <paramref name="rMultiple"/> times
    /// the fee-paid initial risk. Solved exactly on the asymmetric PSE buy/sell legs, then
    /// rounded up to a tradeable tick and capped at the target.
    /// </summary>
    public static decimal ComputeTriggerPrice(
        decimal entry,
        decimal stopLoss,
        decimal target,
        decimal rMultiple)
    {
        var netRisk = PseBoardLotService.ComputeNetRiskPerShare(entry, stopLoss);
        if (entry <= 0m || netRisk <= 0m || target <= entry)
        {
            return PseBoardLotService.RoundToValidTick(target);
        }

        var raw = ((rMultiple * netRisk) +
                   (entry * (1m + PseBoardLotService.BuyLegFeeRate))) /
                  (1m - PseBoardLotService.SellLegFeeRate);

        var price = PseBoardLotService.CeilingToTick(raw);
        if (price < entry)
        {
            price = PseBoardLotService.RoundToValidTick(entry);
        }

        return Math.Min(price, target);
    }


    /// <summary>
    /// "I already own it" check. The position is re-anchored to the reader's own average
    /// cost, so the ATR cut-loss and target describe their money, not today's entry. The
    /// exit ladder mirrors the blotter exactly: stop first, target second, then the +1.5R
    /// EMA20 trail and the +1R break-even lock, and only whole board lots are ever trimmed.
    /// </summary>
    public static PositionAdviceDto EvaluateHolding(
        string ticker,
        decimal averageCost,
        decimal currentPrice,
        int shares,
        decimal atr14,
        decimal? ema20,
        SignalHorizon horizon)
    {
        if (averageCost <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(averageCost), averageCost, "Average cost must be positive.");
        }

        if (currentPrice <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(currentPrice), currentPrice, "Current price must be positive.");
        }

        var profile = HorizonProfile.For(horizon);
        var atr = Math.Max(0m, atr14);
        var rawStop = averageCost - (profile.AtrStopMultiplier * atr);
        var rawTarget = averageCost + (profile.AtrTargetMultiplier * atr);
        var (entry, initialStop, target) = PseBoardLotService.NormalizeTradePlan(
            averageCost,
            rawStop,
            rawTarget);

        var breakEven = PseBoardLotService.ComputeBreakEvenStopPrice(entry);
        var netRiskPerShare = PseBoardLotService.ComputeNetRiskPerShare(entry, initialStop);
        var netPerShare = PseBoardLotService.ComputeRealizedNetPnlPhp(1, entry, currentPrice);
        var unrealizedR = netRiskPerShare > 0m
            ? decimal.Round(netPerShare / netRiskPerShare, 2, MidpointRounding.AwayFromZero)
            : 0m;
        var netReturnPct = decimal.Round(netPerShare / entry * 100m, 2, MidpointRounding.AwayFromZero);
        var unrealizedPhp = shares > 0
            ? decimal.Round(
                PseBoardLotService.ComputeRealizedNetPnlPhp(shares, entry, currentPrice),
                2,
                MidpointRounding.AwayFromZero)
            : (decimal?)null;

        var trailStop = ema20 is decimal ema && ema > 0m
            ? PseBoardLotService.FloorToTick(ema)
            : (decimal?)null;
        if (trailStop is decimal trail && (trail <= initialStop || trail >= currentPrice))
        {
            trailStop = null;
        }

        var suggestedStop = initialStop;
        string action;

        if (currentPrice <= initialStop)
        {
            action = "STOP_BREACHED";
        }
        else if (currentPrice >= target)
        {
            action = "TARGET_REACHED";
        }
        else if (unrealizedR >= TrailTriggerR && trailStop is decimal trailPrice)
        {
            action = "TRAIL_STOP_EMA20";
            suggestedStop = trailPrice;
        }
        else if (unrealizedR >= BreakEvenTriggerR && breakEven > initialStop)
        {
            action = "MOVE_STOP_TO_BREAKEVEN";
            suggestedStop = breakEven;
        }
        else
        {
            action = "HOLD";
        }

        var trimShares = shares > 0 && currentPrice > breakEven
            ? ComputeRecommendedTrimShares(shares, entry, currentPrice)
            : 0;

        var (headline, explanation) = BuildHoldingCopy(
            action,
            currentPrice,
            initialStop,
            target,
            suggestedStop,
            netReturnPct,
            unrealizedR,
            trimShares);

        return new PositionAdviceDto(
            ticker,
            entry,
            currentPrice,
            shares,
            breakEven,
            suggestedStop,
            target,
            netReturnPct,
            unrealizedPhp,
            unrealizedR,
            action,
            headline,
            explanation,
            trimShares);
    }

    /// <summary>
    /// The bank-half instruction the blotter offers, recomputed for a research answer: the
    /// largest slice at or below half that is a whole board lot on the exit price and leaves
    /// a whole board lot at the average cost. Zero means no legal trim exists.
    /// </summary>
    public static int ComputeRecommendedTrimShares(int shares, decimal entryPrice, decimal exitPrice)
    {
        var entryBoardLot = PseBoardLotService.GetBoardLotSize(entryPrice);
        var exitBoardLot = PseBoardLotService.GetBoardLotSize(exitPrice);
        if (shares <= 0 || entryBoardLot <= 0 || exitBoardLot <= 0)
        {
            return 0;
        }

        for (var slice = shares / 2 / exitBoardLot * exitBoardLot;
             slice >= exitBoardLot;
             slice -= exitBoardLot)
        {
            if ((shares - slice) % entryBoardLot == 0)
            {
                return slice;
            }
        }

        return 0;
    }


    private static (string Headline, string Explanation) BuildHoldingCopy(
        string action,
        decimal currentPrice,
        decimal initialStop,
        decimal target,
        decimal suggestedStop,
        decimal netReturnPct,
        decimal unrealizedR,
        int trimShares)
    {
        var move = Math.Abs(netReturnPct);
        var trimNote = trimShares > 0
            ? $" A half-trim of {trimShares:N0} shares is the largest whole board lot that keeps the rest at cost."
            : string.Empty;

        // Impersonal, conditional phrasing. These strings describe where the model's levels sit and
        // what the exit rule would do; they are not personal recommendations to trade.
        return action switch
        {
            "STOP_BREACHED" => (
                "Cut-loss level breached",
                $"The price is PHP {FormatPrice(currentPrice)}, below the PHP {FormatPrice(initialStop)} cut-loss. " +
                "By the model's exit rule the position is closed here, which caps the loss at the planned R."),
            "TARGET_REACHED" => (
                "Target reached",
                $"The price is PHP {FormatPrice(currentPrice)}, at or above the PHP {FormatPrice(target)} target. " +
                "The model's exit rule banks the planned gain at this level."),
            "TRAIL_STOP_EMA20" => (
                "Trend intact — trail the stop",
                $"The position is up {Format(move)}% after fees, about {Format(unrealizedR)}R. The model's trail " +
                $"sits at PHP {FormatPrice(suggestedStop)}, just under the 20-day average, so the open gain stays protected." + trimNote),
            "MOVE_STOP_TO_BREAKEVEN" => (
                "Break-even stop available",
                $"The position is up {Format(move)}% after fees. A stop at PHP {FormatPrice(suggestedStop)} takes the " +
                "trade's fee-adjusted loss to zero." + trimNote),
            _ => (
                "Plan unchanged — no level touched",
                netReturnPct == 0m
                    ? $"Flat after fees at PHP {FormatPrice(currentPrice)}. The cut-loss sits at PHP {FormatPrice(initialStop)}."
                    : $"{(netReturnPct > 0m ? "Up" : "Down")} {Format(move)}% after fees at PHP {FormatPrice(currentPrice)}. " +
                      $"The cut-loss sits at PHP {FormatPrice(initialStop)}.")
        };
    }

    private static string Format(decimal value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private static string FormatPrice(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}

