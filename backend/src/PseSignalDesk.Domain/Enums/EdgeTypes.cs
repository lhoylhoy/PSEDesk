namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// Deterministic actionability verdict for one ranked pick. The server (never the
/// model) decides whether a setup deserves real capital, a reduced size, or no bet
/// at all once the 0.690% round-trip friction and the calibrated win probability are
/// applied. <see cref="WATCH_ONLY"/> picks stay in the list for research context but
/// carry zero risk-sized shares.
/// </summary>
public enum EdgeVerdict
{
    /// <summary>Clears the horizon net R:R gate, the expected-value floor and the probability buffer.</summary>
    TRADE,

    /// <summary>
    /// Positive expected value after fees, but the buffer, a soft trap warning or the
    /// tape argues for a reduced position.
    /// </summary>
    SMALL_SIZE,

    /// <summary>Zero or negative expected value after fees: a NO-BET / PASS.</summary>
    WATCH_ONLY
}

/// <summary>
/// Forward-outcome ledger state for a persisted pick. The resolver grades a pick
/// against the real OHLC sessions that follow its run date, so the desk can measure
/// whether its conviction actually predicted anything.
/// </summary>
public enum SignalOutcomeStatus
{
    /// <summary>No barrier touched yet and the horizon window has not elapsed.</summary>
    OPEN,

    /// <summary>The target barrier was touched before the stop.</summary>
    WIN_TARGET,

    /// <summary>The stop barrier was touched first (same-bar ambiguity resolves here).</summary>
    LOSS_STOP,

    /// <summary>The horizon window expired with a positive net return after fees.</summary>
    EXPIRED_WIN,

    /// <summary>The horizon window expired with a zero or negative net return after fees.</summary>
    EXPIRED_LOSS,

    /// <summary>
    /// The entry price was never traded inside the horizon window, so the order never filled.
    /// The pick is neither a win nor a loss and carries no realized R: grading an unfilled limit
    /// as if it had been bought books a P&L for a position the desk never held.
    /// </summary>
    UNFILLED
}
