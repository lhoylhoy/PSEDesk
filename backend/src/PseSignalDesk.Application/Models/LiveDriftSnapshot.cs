using PseSignalDesk.Domain.Enums;

namespace PseSignalDesk.Application.Models;

/// <summary>
/// The desk's read on whether a promoted book still earns the record it was promoted on.
/// <see cref="Status"/> is a throttle, not a re-pricing: it scales new risk through
/// <c>LiveDriftPolicy.MultiplierFor</c> and never rewrites a published signal.
/// <para>
/// The gaps are measured against the model's own held-out record
/// (<c>TrainedHorizonModel.ValidationWinRate</c> / <c>ValidationExpectancyR</c>), so a book is
/// only judged against the promise it actually made. A positive gap means the live book is
/// behind. When no promoted model is in charge the desk is priced off the structural prior,
/// which has no promoted record to drift from, and the status stays
/// <see cref="LiveDriftStatus.STABLE"/>.
/// </para>
/// </summary>
public sealed record LiveDriftSnapshot(
    LiveDriftStatus Status,
    int GradedPicks,
    decimal RealizedWinRate,
    decimal ExpectedWinRate,
    decimal RollingExpectancyR,
    decimal BaselineExpectancyR,
    decimal WinRateGapPct,
    decimal ExpectancyGapR,
    string? Reason)
{
    /// <summary>Graded picks the rolling window reads at most: recent form, not a lifetime average.</summary>
    public const int WindowPicks = 30;

    /// <summary>No throttle: either the book is on the prior, or it is tracking its record.</summary>
    public static LiveDriftSnapshot Stable(int gradedPicks = 0) =>
        new(LiveDriftStatus.STABLE, gradedPicks, 0m, 0m, 0m, 0m, 0m, 0m, null);
}
