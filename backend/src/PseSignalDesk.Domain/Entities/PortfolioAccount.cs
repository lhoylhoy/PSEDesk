namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// Single-desk capital configuration and cash ledger. One row exists per desk; the API
/// creates a default <c>PHP 100,000</c> account on first read so the blotter never has to
/// guess a bankroll. All risk guardrails are percentages of current equity.
/// </summary>
public class PortfolioAccount
{
    public int Id { get; set; }

    /// <summary>Bankroll the desk started with, kept for the realized-equity curve.</summary>
    public decimal StartingCapitalPhp { get; set; } = 100_000m;

    /// <summary>Uninvested cash after every open position's all-in buy cost is deducted.</summary>
    public decimal CashBalancePhp { get; set; } = 100_000m;

    /// <summary>Maximum equity risked on a single TRADE, in percent (default 1.0%).</summary>
    public decimal MaxRiskPerTradePct { get; set; } = 1.0m;

    /// <summary>Risk multiplier applied to a SMALL_SIZE verdict (default 0.5 = half risk).</summary>
    public decimal SmallSizeRiskMultiplier { get; set; } = 0.5m;

    /// <summary>Maximum single-position notional as a percentage of equity (default 20%).</summary>
    public decimal MaxPositionPct { get; set; } = 20.0m;

    /// <summary>Maximum aggregate open heat across all positions, in percent (default 6%).</summary>
    public decimal MaxPortfolioHeatPct { get; set; } = 6.0m;

    /// <summary>
    /// High-water mark of total equity. The drawdown governor throttles risk when equity
    /// falls from this peak, so it is the anchor of the whole pod-shop risk posture. Null on
    /// an account created before the governor existed; the desk seeds it from the live equity
    /// (or the starting capital, whichever is higher) on first read, because defaulting a
    /// grown book to its starting capital would invent a drawdown that never happened.
    /// </summary>
    public decimal? PeakEquityPhp { get; set; }

    /// <summary>
    /// Maximum share of equity that may sit in one PSE sector (default 35%). Four banks are
    /// one macro bet, not four independent ones, so the book needs a sector ceiling on top of
    /// the single-position cap.
    /// </summary>
    public decimal MaxSectorExposurePct { get; set; } = 35.0m;

    /// <summary>
    /// Maximum number of open positions in one sector (default 2). Stops the desk from
    /// stacking correlated names that would all stop out together. Zero disables the cap.
    /// </summary>
    public int MaxPositionsPerSector { get; set; } = 2;

    /// <summary>
    /// Drawdown (percent from the equity high-water mark) at which the governor cuts every
    /// risk budget by the caution multiplier and refuses new SMALL_SIZE risk (default 5%).
    /// </summary>
    public decimal SoftDrawdownThresholdPct { get; set; } = 5.0m;

    /// <summary>
    /// Drawdown at which the governor halves the risk budget again and halves the portfolio
    /// heat cap (default 10%). Above this the book is in capital-preservation mode.
    /// </summary>
    public decimal HardDrawdownThresholdPct { get; set; } = 10.0m;

    /// <summary>
    /// Annualised volatility target the sizing engine scales against the PSEi's realized
    /// 20-session volatility (default 18%). A 30%-vol tape therefore gets ~0.6x the risk of a
    /// calm one. Never scales risk above 1.0x.
    /// </summary>
    public decimal TargetAnnualVolPct { get; set; } = 18.0m;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
