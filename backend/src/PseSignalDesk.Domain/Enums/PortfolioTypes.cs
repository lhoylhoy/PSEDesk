namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// Lifecycle of one blotter position. <see cref="OPEN"/> positions are marked to market
/// on the latest stored session; <see cref="CLOSED"/> positions carry a settled, fee-paid
/// realized result in PHP and R.
/// </summary>
public enum PortfolioTradeStatus
{
    OPEN,
    CLOSED
}

/// <summary>
/// Server-computed trade-management instruction for an open position. The desk derives it
/// from the stored session (high/low/close, EMA20), the fee-paid break-even price and the
/// horizon holding window — never from a model or from the browser.
/// </summary>
public enum PositionActionSignal
{
    /// <summary>Nothing to do: no barrier touched, below +1R and inside the horizon window.</summary>
    HOLD,

    /// <summary>The latest session traded through the current stop (or closed below it).</summary>
    STOP_BREACHED,

    /// <summary>The latest session reached the profit target.</summary>
    TARGET_REACHED,

    /// <summary>Net unrealized R is at least +1.0 and the stop is still below the fee-paid break-even tick.</summary>
    MOVE_STOP_TO_BREAKEVEN,

    /// <summary>Net unrealized R is at least +1.5 and EMA20 now sits above the current stop.</summary>
    TRAIL_STOP_EMA20,

    /// <summary>The horizon holding window elapsed with less than +0.5R of net progress.</summary>
    TIME_STOP_DUE,

    /// <summary>A fresh hard trap disclosure landed after the entry.</summary>
    TRAP_WARNING
}
