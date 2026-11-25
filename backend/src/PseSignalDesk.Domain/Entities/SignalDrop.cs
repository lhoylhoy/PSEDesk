using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Domain.Entities;

/// <summary>
/// A ticker that was in the previous Top 10 but is not in today's list.
/// </summary>
public class SignalDrop
{
    public int Id { get; set; }

    public int SignalRunId { get; set; }

    public SignalRun? SignalRun { get; set; }

    public string Ticker { get; set; } = string.Empty;

    public int? PreviousRank { get; set; }

    public DropCategory DropCategory { get; set; }

    public string Reason { get; set; } = string.Empty;
}
