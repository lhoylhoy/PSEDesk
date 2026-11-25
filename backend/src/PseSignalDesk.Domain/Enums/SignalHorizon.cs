namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// Signal horizon. Phase 1 produces WEEK signals only; the remaining values are
/// reserved so the persisted schema and API contract stay stable for Phase 2.
/// </summary>
public enum SignalHorizon
{
    DAY,
    WEEK,
    MONTH,
    YEAR
}
