namespace PseSignalDesk.Domain.Services;

/// <summary>
/// One row of the official PSE Revised Trading Rules board lot / price fluctuation
/// table. Both bounds are inclusive; the table has 15 tiers and the finest tick is
/// <c>₱0.0001</c>, which is why prices are rounded to four decimals.
/// </summary>
public sealed record PsePriceBracket(
    decimal MinPrice,
    decimal MaxPriceInclusive,
    decimal TickSize,
    int BoardLot);

/// <summary>
/// The static daily price band the exchange enforces around the reference close:
/// a <c>+50%</c> ceiling and a <c>-30%</c> floor. Both bounds are already tick-rounded
/// (ceiling down, floor up) so they are orderable prices.
/// </summary>
public sealed record PseStaticPriceLimits(
    decimal ReferenceClose,
    decimal StaticCeiling,
    decimal StaticFloor);

/// <summary>
/// One risk-budgeted position: whole exchange board lots plus the fee-adjusted peso
/// risk and reward that position actually carries. Produced by
/// <see cref="PseBoardLotService.SizePositionByRiskBudget"/> so the desk can size by
/// risk instead of by a flat notional.
/// </summary>
public sealed record RiskSizedPosition(
    int BoardLotSize,
    int Shares,
    decimal NotionalPhp,
    decimal NetRiskPhp,
    decimal NetRewardPhp,
    bool LiquidityCapped);

/// <summary>
/// Official PSE board lot and price-increment (tick) rules plus deterministic trade
/// plan helpers. All generated prices must pass through this service before they are
/// persisted so the Top 10 list only ever shows orderable PSE prices.
/// </summary>
public static class PseBoardLotService
{
    /// <summary>
    /// Round-trip PSE friction under the current statutory schedule: the buy leg plus the
    /// sell leg. Used to check that a plan still clears costs after both legs.
    /// <para>
    /// It is <b>not</b> a single symmetric rate: the stock transaction tax is charged on
    /// sales only, so the two legs differ. See <see cref="BuyLegFeeRate"/> and
    /// <see cref="SellLegFeeRate"/>.
    /// </para>
    /// </summary>
    public const decimal RoundTripFeeRate = BuyLegFeeRate + SellLegFeeRate;

    /// <summary>
    /// Buy-leg friction: <c>0.25%</c> commission + <c>12%</c> VAT on that commission,
    /// the <c>0.005%</c> PSE trading fee and the <c>0.01%</c> SCCP fee. The all-in buy cost
    /// of a position is <c>shares * price * (1 + BuyLegFeeRate)</c>.
    /// </summary>
    public const decimal BuyLegFeeRate = 0.00295m;

    /// <summary>
    /// Sell-leg friction: the same broker/exchange charges plus the stock transaction tax,
    /// so a sale nets <c>shares * price * (1 - SellLegFeeRate)</c>.
    /// <para>
    /// The tax is <b>0.10%</b> under Republic Act No. 12214 (the Capital Markets
    /// Efficiency Promotion Act, effective 1 July 2025), down from the pre-CMEPA
    /// <c>0.60%</c>. That makes the sell leg <c>0.25% + 0.03% VAT + 0.005% + 0.01% + 0.10%
    /// = 0.395%</c> and the round trip <c>0.690%</c>.
    /// </para>
    /// <c>BuyLegFeeRate + SellLegFeeRate == RoundTripFeeRate</c>.
    /// </summary>
    public const decimal SellLegFeeRate = 0.00395m;

    /// <summary>Broker commission on either leg, before VAT: <c>0.25%</c> of gross.</summary>
    public const decimal BrokerCommissionRate = 0.0025m;

    /// <summary>VAT charged on the broker commission itself: <c>12%</c>.</summary>
    public const decimal CommissionVatRate = 0.12m;

    /// <summary>The PSE's own trading fee: <c>0.005%</c> of gross on each leg.</summary>
    public const decimal PseTradingFeeRate = 0.00005m;

    /// <summary>Securities Clearing Corporation of the Philippines fee: <c>0.01%</c> per leg.</summary>
    public const decimal SccpFeeRate = 0.0001m;

    /// <summary>
    /// Broker minimum commission. Almost every PSE retail broker charges
    /// <c>PHP 20</c> when <c>0.25%</c> of the clip is smaller than that, so a small ticket pays a
    /// fixed cost the flat-rate model cannot see — on a <c>PHP 2,500</c> clip the floor alone is
    /// <b>0.90% per leg</b>, not the advertised 0.295%.
    /// </summary>
    public const decimal MinimumBrokerCommissionPhp = 20m;

    /// <summary>Stock transaction tax on sales under RA 12214 (CMEPA), effective 1 July 2025.</summary>
    public const decimal StockTransactionTaxRateCurrent = 0.0010m;

    /// <summary>Stock transaction tax on sales before CMEPA took effect: <c>0.60%</c>.</summary>
    public const decimal StockTransactionTaxRatePreCmepa = 0.0060m;

    /// <summary>First session the <c>0.10%</c> stock transaction tax applied.</summary>
    public static readonly DateOnly CmepaEffectiveDate = new(2025, 7, 1);

    /// <summary>
    /// Highest price the desk will accept as orderable. The board lot table is open-ended, but
    /// <c>shares * price</c> must stay inside <see cref="decimal"/> for every downstream ledger
    /// figure, and no PSE name has ever traded near this ceiling.
    /// </summary>
    public const decimal MaximumTradablePrice = 1_000_000m;

    private const decimal LargestTick = 5.00m;

    /// <summary>
    /// The stock transaction tax that actually applied on <paramref name="tradeDate"/>. Passing
    /// null means "today", which is the CMEPA <c>0.10%</c> schedule; any date before
    /// <see cref="CmepaEffectiveDate"/> really paid <c>0.60%</c>, and pricing that history at
    /// today's tax would manufacture roughly <c>0.2R</c> of phantom edge per 5%-stop trade.
    /// </summary>
    public static decimal StockTransactionTaxRate(DateOnly? tradeDate = null) =>
        tradeDate is DateOnly date && date < CmepaEffectiveDate
            ? StockTransactionTaxRatePreCmepa
            : StockTransactionTaxRateCurrent;

    /// <summary>Sell-leg friction as it applied on a date: broker, VAT, exchange, SCCP and the tax.</summary>
    public static decimal SellLegFeeRateOn(DateOnly? tradeDate = null) =>
        BuyLegFeeRate + StockTransactionTaxRate(tradeDate);

    /// <summary>Round-trip statutory friction as it applied on a date.</summary>
    public static decimal RoundTripFeeRateOn(DateOnly? tradeDate = null) =>
        BuyLegFeeRate + SellLegFeeRateOn(tradeDate);

    /// <summary>
    /// True when a price can be ordered and priced without breaking the tick table or the
    /// decimal ledger. Zero, negative and absurd prices are rejected before they can reach
    /// <see cref="GetBracket"/> (where a zero would size a million-share lot).
    /// </summary>
    public static bool IsTradablePrice(decimal price) =>
        price > 0m && price <= MaximumTradablePrice;

    /// <summary>
    /// Peso fee charged on one leg, including the broker minimum commission and the
    /// date-correct stock transaction tax. The commission floor is why this is not simply
    /// <c>gross * rate</c>: below a <c>PHP 8,000</c> gross the fixed <c>PHP 20</c> dominates.
    /// </summary>
    public static decimal ComputeLegFeePhp(int shares, decimal price, bool isSell, DateOnly? tradeDate = null)
    {
        if (shares <= 0 || !IsTradablePrice(price))
        {
            return 0m;
        }

        var gross = shares * price;
        var commission = Math.Max(gross * BrokerCommissionRate, MinimumBrokerCommissionPhp);
        var fee = (commission * (1m + CommissionVatRate)) +
                  (gross * (PseTradingFeeRate + SccpFeeRate));

        if (isSell)
        {
            fee += gross * StockTransactionTaxRate(tradeDate);
        }

        return decimal.Round(fee, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Fill price of a resting stop for one session. A session that opens through the stop fills
    /// at the open (the real, worse, price) instead of at the stop; a session that merely trades
    /// through it fills at the stop. Returns null when the stop was never touched. Targets are
    /// deliberately left at the target: crediting a gap-up open as a better exit would make the
    /// reward look larger than the order would have earned.
    /// </summary>
    public static decimal? ResolveStopFillPrice(decimal stopLoss, decimal sessionOpen, decimal sessionLow)
    {
        if (!IsTradablePrice(stopLoss))
        {
            return null;
        }

        if (IsTradablePrice(sessionOpen) && sessionOpen <= stopLoss)
        {
            return RoundToValidTick(sessionOpen);
        }

        return sessionLow > 0m && sessionLow <= stopLoss ? stopLoss : null;
    }

    /// <summary>
    /// The complete official PSE Revised Trading Rules board lot and tick table
    /// (15 tiers, <c>₱0.0001</c> through <c>₱5,000+</c>).
    /// </summary>
    public static IReadOnlyList<PsePriceBracket> Brackets { get; } = new PsePriceBracket[]
    {
        new(0.0001m, 0.0099m, 0.0001m, 1_000_000),
        new(0.0100m, 0.0490m, 0.0010m, 100_000),
        new(0.0500m, 0.2490m, 0.0010m, 10_000),
        new(0.2500m, 0.4950m, 0.0050m, 10_000),
        new(0.5000m, 4.9900m, 0.0100m, 1_000),
        new(5.0000m, 9.9900m, 0.0100m, 100),
        new(10.0000m, 19.9800m, 0.0200m, 100),
        new(20.0000m, 49.9500m, 0.0500m, 100),
        new(50.0000m, 99.9500m, 0.0500m, 10),
        new(100.0000m, 199.9000m, 0.1000m, 10),
        new(200.0000m, 499.8000m, 0.2000m, 10),
        new(500.0000m, 999.5000m, 0.5000m, 10),
        new(1_000.0000m, 1_999.0000m, 1.0000m, 5),
        new(2_000.0000m, 4_998.0000m, 2.0000m, 5),
        new(5_000.0000m, decimal.MaxValue, 5.0000m, 5)
    };

    /// <summary>
    /// Returns the board lot bracket for a price. Prices that fall between two
    /// tick-grid tiers (for example <c>₱4.995</c>) are assigned to the next tier, whose
    /// tick then rounds them onto an orderable value.
    /// </summary>
    public static PsePriceBracket GetBracket(decimal price)
    {
        if (price < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(price), price, "Price cannot be negative.");
        }

        foreach (var bracket in Brackets)
        {
            if (price <= bracket.MaxPriceInclusive)
            {
                return bracket;
            }
        }

        // decimal.MaxValue already closes the table, but the fallback keeps the
        // contract total if a caller ever narrows the list.
        var last = Brackets[^1];
        return last with { TickSize = LargestTick };
    }

    /// <summary>
    /// Returns the PSE tick size for the given price bracket.
    /// </summary>
    public static decimal GetTickSize(decimal price) => GetBracket(price).TickSize;

    /// <summary>
    /// Returns the exchange minimum order size (board lot) for a price, or 0 for a
    /// non-positive price that cannot be traded.
    /// </summary>
    public static int GetBoardLotSize(decimal price) => price <= 0m ? 0 : GetBracket(price).BoardLot;

    /// <summary>
    /// Hard ceiling on the market-impact share of the slippage estimate. A thin PSE mid-cap
    /// can genuinely cost several percent to cross, but a single pre-trade estimate above
    /// 1.5% would swamp the fee model instead of informing it.
    /// </summary>
    public const decimal MaxMarketImpactRate = 0.015m;

    /// <summary>
    /// Square-root impact coefficient. <c>0.10 * ATR% * sqrt(participation)</c> reproduces
    /// the empirical "cost grows with the square root of size" shape that agency desks use
    /// for pre-trade estimates without pretending to a calibrated microstructure model.
    /// </summary>
    public const decimal MarketImpactCoefficient = 0.10m;

    /// <summary>
    /// One tick of quote spread as a fraction of price: the cost of crossing the bid-ask
    /// once. On the coarse PSE brackets this is brutal — a <c>PHP 0.50</c> name ticks at
    /// <c>PHP 0.01</c>, i.e. <b>2.0% per crossing</b> — which is exactly the drag a
    /// fee-only R:R hides. Returns 0 for a non-positive price.
    /// </summary>
    public static decimal ComputeTickSpreadRate(decimal price)
    {
        if (price <= 0m)
        {
            return 0m;
        }

        return GetTickSize(price) / price;
    }

    /// <summary>
    /// A round trip crosses the spread twice (buy the offer, sell the bid), so the spread
    /// component charged against one back-and-forth is <b>two</b> ticks, not one. Charging a
    /// single tick would understate exactly the wide-tick names this exists to flag.
    /// </summary>
    public static decimal ComputeRoundTripTickSpreadRate(decimal price) =>
        ComputeTickSpreadRate(price) * 2m;

    /// <summary>
    /// Order notional as a fraction of the name's 20-day turnover. Null-safe: a missing
    /// turnover means "unknown participation", never an implied zero.
    /// </summary>
    public static decimal? ComputeAdvParticipationRate(decimal orderNotionalPhp, decimal avgTurnover20dPhp)
    {
        if (orderNotionalPhp <= 0m || avgTurnover20dPhp <= 0m)
        {
            return null;
        }

        return decimal.Round(orderNotionalPhp / avgTurnover20dPhp, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Pre-trade slippage estimate as a fraction of notional:
    /// <c>2 * tick(P)/P + min(1.5%, 0.10 * ATR% * sqrt(notional/ADV))</c>.
    /// The tick term is the guaranteed round-trip spread cost; the square-root term is the
    /// size-dependent market impact. Without ATR the impact term is zero (only the spread is
    /// known), and without turnover only the spread is charged — neither is invented.
    /// </summary>
    public static decimal ComputeEstimatedSlippageRate(
        decimal entryPrice,
        decimal orderNotionalPhp,
        decimal avgTurnover20dPhp,
        decimal? atrPct = null)
    {
        if (entryPrice <= 0m)
        {
            return 0m;
        }

        var spreadRate = ComputeRoundTripTickSpreadRate(entryPrice);
        var participation = ComputeAdvParticipationRate(orderNotionalPhp, avgTurnover20dPhp);

        if (participation is not decimal participationRate || participationRate <= 0m)
        {
            return decimal.Round(spreadRate, 6, MidpointRounding.AwayFromZero);
        }

        var volatility = atrPct is decimal value && value > 0m ? value / 100m : 0m;
        var impact = MarketImpactCoefficient * volatility * (decimal)Math.Sqrt((double)participationRate);
        impact = Math.Clamp(impact, 0m, MaxMarketImpactRate);

        return decimal.Round(spreadRate + impact, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The fee rate to charge in the expectancy maths once slippage is included:
    /// the statutory round trip (<c>0.690%</c>) plus the spread and impact estimate, clamped so
    /// a pathological estimate can never invert the plan geometry. Feed it to
    /// <see cref="ComputeNetRewardPerShare"/>, <see cref="ComputeNetRiskPerShare"/> and
    /// <see cref="ComputeBreakEvenWinRate"/>.
    /// </summary>
    public static decimal ComputeEffectiveRoundTripFeeRate(
        decimal entryPrice,
        decimal orderNotionalPhp,
        decimal avgTurnover20dPhp,
        decimal? atrPct = null,
        DateOnly? tradeDate = null)
    {
        var statutory = RoundTripFeeRateOn(tradeDate);
        var effective = statutory +
                        ComputeEstimatedSlippageRate(entryPrice, orderNotionalPhp, avgTurnover20dPhp, atrPct);

        return decimal.Round(Math.Clamp(effective, statutory, 0.25m), 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rounds a price to the nearest valid PSE tick for its bracket and four decimals
    /// (sub-centavo tiers are real). Midpoints round away from zero, the more
    /// conservative fill price.
    /// </summary>
    public static decimal RoundToValidTick(decimal price)
    {
        if (price <= 0m)
        {
            return 0m;
        }

        var tick = GetTickSize(price);
        var rounded = Math.Round(price / tick, 0, MidpointRounding.AwayFromZero) * tick;

        // Rounding can push a value into the next bracket whose tick is coarser.
        // Re-round with the new bracket's tick until the price and tick agree.
        var recalculatedTick = GetTickSize(rounded);
        if (recalculatedTick != tick)
        {
            rounded = Math.Round(price / recalculatedTick, 0, MidpointRounding.AwayFromZero) * recalculatedTick;
        }

        return decimal.Round(rounded, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rounds a price down to the tick grid. Used for the static ceiling: a ceiling
    /// must never sit above the exchange's <c>+50%</c> boundary.
    /// </summary>
    public static decimal FloorToTick(decimal price)
    {
        if (price <= 0m)
        {
            return 0m;
        }

        var tick = GetTickSize(price);
        var rounded = Math.Floor(price / tick) * tick;
        return decimal.Round(rounded <= 0m ? tick : rounded, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Rounds a price up to the tick grid. Used for the static floor: a floor must
    /// never sit below the exchange's <c>-30%</c> boundary.
    /// </summary>
    public static decimal CeilingToTick(decimal price)
    {
        if (price <= 0m)
        {
            return 0m;
        }

        var tick = GetTickSize(price);
        var rounded = Math.Ceiling(price / tick) * tick;
        return decimal.Round(rounded, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Returns the number of shares for a notional budget, floored to whole board lots.
    /// Zero means the budget cannot buy a single board lot at that price.
    /// </summary>
    public static int RoundSharesToBoardLot(decimal budgetPhp, decimal entryPrice)
    {
        if (budgetPhp <= 0m || entryPrice <= 0m)
        {
            return 0;
        }

        var boardLot = GetBoardLotSize(entryPrice);
        if (boardLot <= 0)
        {
            return 0;
        }

        var rawShares = Math.Floor(budgetPhp / entryPrice);
        if (rawShares < boardLot)
        {
            return 0;
        }

        var shares = Math.Floor(rawShares / boardLot) * boardLot;

        return shares > int.MaxValue ? int.MaxValue : (int)shares;
    }

    /// <summary>
    /// Splits a requested round-trip fee rate into its buy and sell legs while preserving the
    /// statutory asymmetry: the sell leg always carries the stock transaction tax.
    /// <para>
    /// At the default <see cref="RoundTripFeeRate"/> the result is exactly
    /// <see cref="BuyLegFeeRate"/> / <see cref="SellLegFeeRate"/>, so the expectancy maths and
    /// the blotter's settled P&amp;L agree. A caller that supplies extra friction (slippage)
    /// pays it half on each leg; a caller that supplies less scales both legs proportionally.
    /// </para>
    /// </summary>
    public static (decimal BuyRate, decimal SellRate) SplitRoundTripFee(decimal roundTripFeeRate)
    {
        var clamped = Math.Clamp(roundTripFeeRate, 0m, 0.5m);

        if (clamped >= RoundTripFeeRate)
        {
            var extra = (clamped - RoundTripFeeRate) / 2m;
            return (BuyLegFeeRate + extra, SellLegFeeRate + extra);
        }

        var scale = clamped / RoundTripFeeRate;
        return (BuyLegFeeRate * scale, SellLegFeeRate * scale);
    }

    /// <summary>
    /// Date-aware split: the extra friction (slippage) is split evenly on top of the statutory
    /// schedule that applied on <paramref name="tradeDate"/>, and a rate below it is scaled down
    /// proportionally. Used by the replay and the trainer, where the tax was higher.
    /// </summary>
    public static (decimal BuyRate, decimal SellRate) SplitRoundTripFee(
        decimal roundTripFeeRate,
        DateOnly? tradeDate)
    {
        var baseline = RoundTripFeeRateOn(tradeDate);
        var clamped = Math.Clamp(roundTripFeeRate, 0m, 0.5m);
        var sellLeg = SellLegFeeRateOn(tradeDate);

        if (clamped >= baseline)
        {
            var extra = (clamped - baseline) / 2m;
            return (BuyLegFeeRate + extra, sellLeg + extra);
        }

        var scale = baseline > 0m ? clamped / baseline : 0m;
        return (BuyLegFeeRate * scale, sellLeg * scale);
    }

    /// <summary>
    /// Net reward per share after the round-trip friction on both legs. Returns 0 when
    /// the target is not above the entry or the move cannot pay the fees.
    /// <para>
    /// Each leg pays its own rate: the exit is charged the sell leg and the entry pays the buy
    /// leg. Charging the whole round trip against both prices doubled the real friction and
    /// disagreed with <see cref="ComputeRealizedNetPnlPhp"/>; the two now agree.
    /// </para>
    /// </summary>
    public static decimal ComputeNetRewardPerShare(
        decimal entry,
        decimal target,
        decimal roundTripFeeRate = RoundTripFeeRate)
    {
        if (entry <= 0m || target <= entry)
        {
            return 0m;
        }

        var (buyRate, sellRate) = SplitRoundTripFee(roundTripFeeRate);
        var netReward = (target * (1m - sellRate)) - (entry * (1m + buyRate));

        return netReward <= 0m ? 0m : decimal.Round(netReward, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Net risk per share: the all-in buy cost of one share minus the net proceeds of selling
    /// it at the stop. The buy leg pays the commission/exchange charges and the stop exit pays
    /// those plus the stock transaction tax, so the loss is measured the way the blotter
    /// measures it.
    /// </summary>
    public static decimal ComputeNetRiskPerShare(
        decimal entry,
        decimal stopLoss,
        decimal roundTripFeeRate = RoundTripFeeRate)
    {
        if (entry <= 0m || stopLoss <= 0m || stopLoss >= entry)
        {
            return 0m;
        }

        var (buyRate, sellRate) = SplitRoundTripFee(roundTripFeeRate);
        var netRisk = (entry * (1m + buyRate)) - (stopLoss * (1m - sellRate));

        return decimal.Round(netRisk, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Fee-adjusted break-even win rate: the probability of touching the target first at
    /// which the strategy's expectancy is exactly zero. A textbook gross 1:2.0 swing is
    /// only about 1:1.20 net, so its break-even rate is far above the naive 33.3%.
    /// </summary>
    public static decimal ComputeBreakEvenWinRate(
        decimal entry,
        decimal stopLoss,
        decimal target,
        decimal roundTripFeeRate = RoundTripFeeRate)
    {
        var netReward = ComputeNetRewardPerShare(entry, target, roundTripFeeRate);
        var netRisk = ComputeNetRiskPerShare(entry, stopLoss, roundTripFeeRate);

        if (netRisk <= 0m || netReward <= 0m)
        {
            return 1m;
        }

        return decimal.Round(netRisk / (netReward + netRisk), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Expected value of one unit of risk (in R) for a win probability and a net
    /// reward-to-risk ratio: <c>p * R - (1 - p)</c>. Positive means the bet is worth
    /// taking after fees; the minimum R:R gate alone cannot tell you that.
    /// </summary>
    public static decimal ComputeExpectedValueR(decimal winProbability, decimal netRiskReward)
    {
        var probability = Math.Clamp(winProbability, 0m, 1m);
        var reward = Math.Max(0m, netRiskReward);
        var expectedValue = (probability * reward) - (1m - probability);

        return decimal.Round(expectedValue, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Expected value of one unit of risk when the two branches pay differently:
    /// <c>p * averageWinR - (1 - p) * averageLossR</c>. Use this whenever the payoff
    /// distribution is known, because a win rate alone cannot distinguish a target hit from a
    /// time-expired scratch.
    /// </summary>
    public static decimal ComputeExpectedValueR(
        decimal winProbability,
        decimal averageWinR,
        decimal averageLossR)
    {
        var probability = Math.Clamp(winProbability, 0m, 1m);
        var reward = Math.Max(0m, averageWinR);
        var loss = Math.Max(0m, averageLossR);
        var expectedValue = (probability * reward) - ((1m - probability) * loss);

        return decimal.Round(expectedValue, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Quarter-Kelly stake as a fraction of equity, clamped to a defensive maximum.
    /// Full Kelly assumes the probability estimate is exact; quarter Kelly keeps the
    /// sizing honest when the win rate is a shrunk, model-assisted guess.
    /// </summary>
    public static decimal ComputeQuarterKellyFraction(
        decimal winProbability,
        decimal netRiskReward,
        decimal maxFraction = 0.20m)
    {
        if (netRiskReward <= 0m)
        {
            return 0m;
        }

        var probability = Math.Clamp(winProbability, 0m, 1m);
        var edge = probability - ((1m - probability) / netRiskReward);
        var fraction = 0.25m * Math.Max(0m, edge);
        var capped = Math.Clamp(fraction, 0m, Math.Max(0m, maxFraction));

        return decimal.Round(capped, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Converts an account equity and a per-trade risk budget into whole PSE board lots,
    /// using the all-in (stop + fees) risk per share. The result is additionally capped by
    /// a maximum position notional and by a maximum share of the name's 20-day turnover,
    /// so a thin name can never be oversized even when the risk budget would allow it.
    /// </summary>
    public static RiskSizedPosition SizePositionByRiskBudget(
        decimal accountEquityPhp,
        decimal maxRiskFraction,
        decimal entryPrice,
        decimal stopLoss,
        decimal targetPrice,
        decimal avgTurnover20dPhp,
        decimal maxPositionFraction = 0.20m,
        decimal maxLiquidityFraction = 0.02m,
        decimal roundTripFeeRate = RoundTripFeeRate)
    {
        var boardLot = entryPrice > 0m ? GetBoardLotSize(entryPrice) : 0;
        var empty = new RiskSizedPosition(boardLot, 0, 0m, 0m, 0m, false);

        if (accountEquityPhp <= 0m || boardLot <= 0)
        {
            return empty;
        }

        var netRiskPerShare = ComputeNetRiskPerShare(entryPrice, stopLoss, roundTripFeeRate);
        if (netRiskPerShare <= 0m)
        {
            return empty;
        }

        var netRewardPerShare = ComputeNetRewardPerShare(entryPrice, targetPrice, roundTripFeeRate);

        var riskBudgetPhp = accountEquityPhp * Math.Clamp(maxRiskFraction, 0m, 1m);
        var riskShares = (long)Math.Floor(riskBudgetPhp / netRiskPerShare);

        var notionalCapPhp = accountEquityPhp * Math.Clamp(maxPositionFraction, 0m, 1m);
        var notionalShares = (long)Math.Floor(notionalCapPhp / entryPrice);

        // An unknown turnover figure must not silently disable the trade; it simply
        // means the liquidity cap cannot constrain this position.
        var hasLiquidityCap = avgTurnover20dPhp > 0m && maxLiquidityFraction > 0m;
        var liquidityShares = hasLiquidityCap
            ? (long)Math.Floor(avgTurnover20dPhp * Math.Clamp(maxLiquidityFraction, 0m, 1m) / entryPrice)
            : long.MaxValue;

        var bindingShares = Math.Min(riskShares, Math.Min(notionalShares, liquidityShares));
        if (bindingShares < boardLot)
        {
            return empty;
        }

        var lots = bindingShares / boardLot;
        var shares = (int)Math.Min(lots * boardLot, int.MaxValue);
        var liquidityCapped = hasLiquidityCap &&
                              liquidityShares < riskShares &&
                              liquidityShares < notionalShares;

        return new RiskSizedPosition(
            boardLot,
            shares,
            decimal.Round(shares * entryPrice, 2, MidpointRounding.AwayFromZero),
            decimal.Round(shares * netRiskPerShare, 2, MidpointRounding.AwayFromZero),
            decimal.Round(shares * netRewardPerShare, 2, MidpointRounding.AwayFromZero),
            liquidityCapped);
    }

    /// <summary>
    /// All-in cash cost of buying <paramref name="shares"/> at
    /// <paramref name="entryPrice"/>, including the buy-leg fees. This is the amount that
    /// leaves the account, so it is what the cash ledger debits.
    /// <para>
    /// The fees are charged at the peso level, not as a flat rate, because the broker's
    /// <c>PHP 20</c> minimum commission dominates a small clip: a PHP 2,500 buy really costs
    /// about PHP 22.55 in fees, not PHP 7.38.
    /// </para>
    /// </summary>
    public static decimal ComputeBuyTotalCostPhp(int shares, decimal entryPrice, DateOnly? tradeDate = null)
    {
        if (shares <= 0 || !IsTradablePrice(entryPrice))
        {
            return 0m;
        }

        var gross = shares * entryPrice;
        var fees = ComputeLegFeePhp(shares, entryPrice, isSell: false, tradeDate);

        return decimal.Round(gross + fees, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Cash received from selling <paramref name="shares"/> at
    /// <paramref name="exitPrice"/>, after the sell-leg fees (including the stock
    /// transaction tax that applied on <paramref name="tradeDate"/>).
    /// </summary>
    public static decimal ComputeSellNetProceedsPhp(int shares, decimal exitPrice, DateOnly? tradeDate = null)
    {
        if (shares <= 0 || !IsTradablePrice(exitPrice))
        {
            return 0m;
        }

        var gross = shares * exitPrice;
        var fees = ComputeLegFeePhp(shares, exitPrice, isSell: true, tradeDate);

        return decimal.Round(gross - fees, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Settled peso risk of one clip: the cash that leaves the account minus the cash a stop-out
    /// returns. Unlike <see cref="ComputeNetRiskPerShare"/> this is exact under the minimum
    /// commission, because a fixed fee is not divisible across shares.
    /// </summary>
    public static decimal ComputeNetRiskPhp(int shares, decimal entryPrice, decimal stopLoss, DateOnly? tradeDate = null)
    {
        if (shares <= 0 || !IsTradablePrice(entryPrice) || stopLoss <= 0m || stopLoss >= entryPrice)
        {
            return 0m;
        }

        return decimal.Round(
            ComputeBuyTotalCostPhp(shares, entryPrice, tradeDate) -
            ComputeSellNetProceedsPhp(shares, stopLoss, tradeDate),
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>Peso reward of one clip, net of both legs' fees.</summary>
    public static decimal ComputeNetRewardPhp(int shares, decimal entryPrice, decimal targetPrice, DateOnly? tradeDate = null)
    {
        if (shares <= 0 || !IsTradablePrice(entryPrice) || targetPrice <= entryPrice)
        {
            return 0m;
        }

        return decimal.Round(
            ComputeSellNetProceedsPhp(shares, targetPrice, tradeDate) -
            ComputeBuyTotalCostPhp(shares, entryPrice, tradeDate),
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Fee-adjusted reward-to-risk of a clip, measured in pesos so the minimum commission is
    /// charged once per leg instead of being smeared across the per-share spread.
    /// </summary>
    public static decimal ComputeNetRiskRewardPeso(
        int shares,
        decimal entryPrice,
        decimal stopLoss,
        decimal targetPrice,
        DateOnly? tradeDate = null)
    {
        var risk = ComputeNetRiskPhp(shares, entryPrice, stopLoss, tradeDate);
        var reward = ComputeNetRewardPhp(shares, entryPrice, targetPrice, tradeDate);

        if (risk <= 0m || reward <= 0m)
        {
            return 0m;
        }

        return decimal.Round(reward / risk, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Settled peso P&amp;L of a round trip whose legs settled on different dates, so the buy leg
    /// pays the commission schedule of <paramref name="entryDate"/> and the sale pays the stock
    /// transaction tax that applied on <paramref name="exitDate"/>. A position opened in 2024 and
    /// closed in 2026 crosses the 1 July 2025 STT cut and must not be taxed at either rate for
    /// both legs.
    /// </summary>
    public static decimal ComputeRealizedNetPnlPhp(
        int shares,
        decimal entryPrice,
        decimal exitPrice,
        DateOnly? entryDate,
        DateOnly? exitDate)
    {
        if (shares <= 0 || !IsTradablePrice(entryPrice) || !IsTradablePrice(exitPrice))
        {
            return 0m;
        }

        return decimal.Round(
            ComputeSellNetProceedsPhp(shares, exitPrice, exitDate) -
            ComputeBuyTotalCostPhp(shares, entryPrice, entryDate),
            2,
            MidpointRounding.AwayFromZero);
    }


    /// <summary>
    /// Settled peso profit or loss for a completed round trip: sell proceeds net of the
    /// sell leg minus the all-in buy cost. This is the only number the blotter should
    /// present as realized P&amp;L.
    /// </summary>
    public static decimal ComputeRealizedNetPnlPhp(
        int shares,
        decimal entryPrice,
        decimal exitPrice,
        DateOnly? tradeDate = null)
    {
        if (shares <= 0 || !IsTradablePrice(entryPrice) || !IsTradablePrice(exitPrice))
        {
            return 0m;
        }

        return decimal.Round(
            ComputeSellNetProceedsPhp(shares, exitPrice, tradeDate) -
            ComputeBuyTotalCostPhp(shares, entryPrice, tradeDate),
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The exit price at which a position is exactly flat after both fee legs. Rounded up
    /// to the tick grid so the scratch exit never leaves a residual loss: at <c>+1R</c> a
    /// trader can move the stop here and the worst case becomes a fee-free scratch.
    /// </summary>
    public static decimal ComputeBreakEvenStopPrice(decimal entryPrice)
    {
        if (entryPrice <= 0m)
        {
            return 0m;
        }

        var raw = entryPrice * (1m + BuyLegFeeRate) / (1m - SellLegFeeRate);
        return CeilingToTick(raw);
    }

    /// <summary>
    /// Exact break-even exit for an actual clip. The per-share formula above is the right
    /// continuous answer, but a fixed minimum commission can push the true break-even a tick or
    /// two higher on a small clip, so this walks the tick grid up until the settled P&amp;L is no
    /// longer negative.
    /// </summary>
    public static decimal ComputeBreakEvenStopPrice(int shares, decimal entryPrice, DateOnly? tradeDate = null)
    {
        if (shares <= 0 || !IsTradablePrice(entryPrice))
        {
            return 0m;
        }

        var buyCost = ComputeBuyTotalCostPhp(shares, entryPrice, tradeDate);
        var candidate = ComputeBreakEvenStopPrice(entryPrice);

        for (var guard = 0; guard < 512; guard++)
        {
            if (ComputeSellNetProceedsPhp(shares, candidate, tradeDate) >= buyCost)
            {
                return candidate;
            }

            var next = decimal.Round(candidate + GetTickSize(candidate), 4, MidpointRounding.AwayFromZero);

            if (!IsTradablePrice(next) || next <= candidate)
            {
                return candidate;
            }

            candidate = next;
        }

        return candidate;
    }

    /// <summary>
    /// Net result of <b>one share's</b> round trip, before the broker minimum commission is
    /// applied. This is the per-share identity the risk and R maths is built on; use it whenever
    /// a per-share number is needed (unrealized R, break-even geometry). Do <b>not</b> simulate a
    /// one-share trade through <see cref="ComputeRealizedNetPnlPhp"/> instead: a single share pays
    /// the full <c>PHP 22.40</c> minimum commission and the result stops describing a share.
    /// </summary>
    public static decimal ComputeNetPerSharePnl(
        decimal entry,
        decimal exit,
        decimal roundTripFeeRate = RoundTripFeeRate)
    {
        if (!IsTradablePrice(entry) || !IsTradablePrice(exit))
        {
            return 0m;
        }

        var (buyRate, sellRate) = SplitRoundTripFee(roundTripFeeRate);

        return decimal.Round((exit * (1m - sellRate)) - (entry * (1m + buyRate)), 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// All-in peso risk of one position at entry: the money that leaves the account minus
    /// the cash a stop-out returns. This is the denominator of the realized R.
    /// </summary>
    public static decimal ComputeInitialNetRiskPhp(int shares, decimal entryPrice, decimal stopLoss) =>
        ComputeInitialNetRiskPhp(shares, entryPrice, stopLoss, null);

    /// <summary>
    /// All-in peso risk of one position at entry, priced on the friction schedule of the entry
    /// date. The stop leg is charged the same date's sell schedule because the exit date is not
    /// known when the risk is booked; before 1 July 2025 that over-states the tax and therefore
    /// under-states the R of the trade — the conservative direction.
    /// </summary>
    public static decimal ComputeInitialNetRiskPhp(
        int shares,
        decimal entryPrice,
        decimal stopLoss,
        DateOnly? tradeDate)
    {
        if (shares <= 0 || entryPrice <= 0m || stopLoss <= 0m || stopLoss >= entryPrice)
        {
            return 0m;
        }

        return decimal.Round(
            ComputeBuyTotalCostPhp(shares, entryPrice, tradeDate) -
            ComputeSellNetProceedsPhp(shares, stopLoss, tradeDate),
            2,
            MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Computes the exchange's static daily price band for a reference close:
    /// <c>+50%</c> ceiling rounded down and <c>-30%</c> floor rounded up.
    /// </summary>
    public static PseStaticPriceLimits GetStaticPriceLimits(decimal referenceClose)
    {
        if (referenceClose <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(referenceClose),
                referenceClose,
                "Reference close must be positive.");
        }

        var ceiling = FloorToTick(referenceClose * 1.5m);
        var floor = CeilingToTick(referenceClose * 0.7m);

        // A pathological tick table could collide the two bounds; keep the band valid.
        if (floor > ceiling)
        {
            floor = ceiling;
        }

        return new PseStaticPriceLimits(
            decimal.Round(referenceClose, 4, MidpointRounding.AwayFromZero),
            ceiling,
            floor);
    }

    /// <summary>
    /// Checks entry, stop and target against the static daily band. Returns human
    /// readable violations (empty when the plan is tradable inside the band).
    /// </summary>
    public static IReadOnlyList<string> ValidateAgainstStaticThresholds(
        decimal referenceClose,
        decimal entry,
        decimal stopLoss,
        decimal target)
    {
        var limits = GetStaticPriceLimits(referenceClose);
        var violations = new List<string>(4);

        if (entry > limits.StaticCeiling)
        {
            violations.Add(
                $"Entry {entry:0.####} is above the static +50% ceiling of {limits.StaticCeiling:0.####}.");
        }

        if (entry < limits.StaticFloor)
        {
            violations.Add(
                $"Entry {entry:0.####} is below the static -30% floor of {limits.StaticFloor:0.####}.");
        }

        if (target > limits.StaticCeiling)
        {
            violations.Add(
                $"Target {target:0.####} is above the static +50% ceiling of {limits.StaticCeiling:0.####}.");
        }

        if (stopLoss < limits.StaticFloor)
        {
            violations.Add(
                $"Stop {stopLoss:0.####} is below the static -30% floor of {limits.StaticFloor:0.####}.");
        }

        return violations;
    }

    /// <summary>
    /// Pulls a price inside the static daily band and re-lands it on the tick grid.
    /// Used for the intraday horizon, where the exchange would reject an out-of-band
    /// order outright.
    /// </summary>
    public static decimal ClampToStaticLimits(decimal price, PseStaticPriceLimits limits)
    {
        if (price <= 0m)
        {
            return 0m;
        }

        var clampLow = limits.StaticFloor > 0m ? limits.StaticFloor : price;
        var clampHigh = limits.StaticCeiling > 0m ? limits.StaticCeiling : price;
        var bounded = Math.Min(Math.Max(price, clampLow), clampHigh);

        return RoundToValidTick(bounded);
    }

    /// <summary>
    /// Reward-to-risk ratio using the entry as the denominator pivot.
    /// Returns 0 when the stop is not strictly below the entry.
    /// </summary>
    public static decimal ComputeRiskReward(decimal entry, decimal stopLoss, decimal target)
    {
        if (entry <= 0m || entry - stopLoss <= 0m)
        {
            return 0m;
        }

        var ratio = (target - entry) / (entry - stopLoss);
        return ratio <= 0m ? 0m : decimal.Round(ratio, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Reward-to-risk after transaction costs. The desk uses this as the honest gate for
    /// intraday ideas: a gross 1:2.0 can be well under 1:2.0 net. Both legs are priced the way
    /// the blotter settles them — the exit pays the sell leg (including the stock transaction
    /// tax) and the entry pays the buy leg.
    /// </summary>
    public static decimal ComputeNetRiskReward(
        decimal entry,
        decimal stopLoss,
        decimal target,
        decimal roundTripFeeRate = RoundTripFeeRate)
    {
        if (entry <= 0m || entry - stopLoss <= 0m || target <= entry)
        {
            return 0m;
        }

        var (buyRate, sellRate) = SplitRoundTripFee(roundTripFeeRate);

        var netReward = (target * (1m - sellRate)) - (entry * (1m + buyRate));
        var netRisk = (entry * (1m + buyRate)) - (stopLoss * (1m - sellRate));

        if (netRisk <= 0m || netReward <= 0m)
        {
            return 0m;
        }

        return decimal.Round(netReward / netRisk, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Board-lot rounds a trade plan and guarantees the invariant
    /// <c>target &gt; entry &gt; stop</c> by nudging whole ticks when needed.
    /// </summary>
    public static (decimal Entry, decimal StopLoss, decimal Target) NormalizeTradePlan(
        decimal entry,
        decimal stopLoss,
        decimal target)
    {
        var normalizedEntry = RoundToValidTick(entry);
        if (normalizedEntry <= 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(entry), entry, "Entry must be positive.");
        }

        var entryTick = GetTickSize(normalizedEntry);

        var normalizedStop = RoundToValidTick(stopLoss);
        if (normalizedStop <= 0m || normalizedStop >= normalizedEntry)
        {
            // Fall back to exactly one tick beneath the entry whenever the model
            // supplied a stop on the wrong side of the entry.
            var oneTickBelow = decimal.Round(normalizedEntry - entryTick, 4, MidpointRounding.AwayFromZero);
            normalizedStop = oneTickBelow > 0m
                ? oneTickBelow
                : decimal.Round(normalizedEntry * 0.5m, 4, MidpointRounding.AwayFromZero);
        }

        var normalizedTarget = RoundToValidTick(target);
        var guard = 0;
        while (normalizedTarget <= normalizedEntry && guard++ < 32)
        {
            var candidate = decimal.Round(normalizedEntry + entryTick, 4, MidpointRounding.AwayFromZero);

            // Adding a tick can cross into a coarser bracket; re-round against it.
            var crossedTick = GetTickSize(candidate);
            normalizedTarget = crossedTick == entryTick
                ? candidate
                : RoundToValidTick(candidate);
            entryTick = GetTickSize(normalizedEntry);
        }

        return (normalizedEntry, normalizedStop, normalizedTarget);
    }

    /// <summary>
    /// Clamps a plan inside the static band and then normalizes it, so an intraday
    /// recommendation can never print an entry the exchange would reject.
    /// </summary>
    public static (decimal Entry, decimal StopLoss, decimal Target) NormalizeTradePlanWithinStaticLimits(
        decimal referenceClose,
        decimal entry,
        decimal stopLoss,
        decimal target)
    {
        var limits = GetStaticPriceLimits(referenceClose);

        return NormalizeTradePlan(
            ClampToStaticLimits(entry, limits),
            ClampToStaticLimits(stopLoss, limits),
            ClampToStaticLimits(target, limits));
    }
}
