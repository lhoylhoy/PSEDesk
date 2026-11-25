namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// PSE trading session phases in Asia/Manila time, as defined by the Revised Trading
/// Rules (pre-open, morning continuous, recess, afternoon continuous, pre-close, the
/// run-off / trading-at-last window and the closed state).
/// </summary>
public enum PseMarketPhase
{
    PRE_OPEN,
    MORNING_CONTINUOUS,
    RECESS,
    AFTERNOON_CONTINUOUS,
    PRE_CLOSE,
    RUN_OFF,
    CLOSED
}
