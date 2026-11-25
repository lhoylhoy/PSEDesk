namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// Reason a ticker left the Top 10 versus the previous trading day.
/// </summary>
public enum DropCategory
{
    INVALIDATED,
    TARGET_HIT,
    DISPLACED
}
