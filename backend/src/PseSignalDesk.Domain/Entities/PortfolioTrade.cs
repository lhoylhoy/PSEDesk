using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// One real or paper position the desk has taken. Every monetary field is the
/// fee-paid PSE reality: the buy cost includes the buy leg (commission + VAT + PSE fee +
/// SCCP), the sell proceeds deduct the sell leg (including the stock transaction tax),
/// and the realized result is the difference. Board lots and tick-valid prices are
/// enforced before a trade is persisted.
/// </summary>
public class PortfolioTrade
{
    public int Id { get; set; }

    public string Ticker { get; set; } = string.Empty;

    public string CompanyName { get; set; } = string.Empty;

    public string Sector { get; set; } = string.Empty;

    public SignalHorizon Horizon { get; set; } = SignalHorizon.WEEK;

    /// <summary>Verdict the position was opened under; drives the risk multiplier.</summary>
    public EdgeVerdict EdgeVerdict { get; set; } = EdgeVerdict.TRADE;

    /// <summary>The signal item this position was opened from, when it came from a run.</summary>
    public int? SignalItemId { get; set; }

    public PortfolioTradeStatus Status { get; set; } = PortfolioTradeStatus.OPEN;

    public DateOnly OpenedDate { get; set; }

    public int Shares { get; set; }

    public decimal EntryPrice { get; set; }

    public decimal InitialStopLoss { get; set; }

    /// <summary>Live stop the trader can trail to break-even or EMA20.</summary>
    public decimal CurrentStopLoss { get; set; }

    public decimal TargetPrice { get; set; }

    /// <summary>All-in cash outlay including the buy-leg fees.</summary>
    public decimal BuyCostPhp { get; set; }

    /// <summary>All-in peso risk at entry: buy cost minus the fee-paid stop exit.</summary>
    public decimal InitialNetRiskPhp { get; set; }

    public DateOnly? ClosedDate { get; set; }

    public decimal? ExitPrice { get; set; }

    /// <summary>Cash received after the sell-leg fees.</summary>
    public decimal? SellProceedsPhp { get; set; }

    public decimal? RealizedNetPnlPhp { get; set; }

    /// <summary>Realized result in units of <see cref="InitialNetRiskPhp"/>.</summary>
    public decimal? RealizedNetR { get; set; }

    /// <summary>
    /// How many times this position has been legally pyramided into (0, 1 or 2). Each add
    /// averages the entry up, so the desk keeps the count to keep the exposure honest.
    /// </summary>
    public int PyramidCount { get; set; }

    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
