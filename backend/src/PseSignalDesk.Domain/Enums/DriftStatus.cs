namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// Day-over-day movement of a ticker inside the Top 10 list.
/// </summary>
public enum DriftStatus
{
    NEW,
    HELD,
    UPGRADED,
    DOWNGRADED
}
