namespace PseSignalDesk.Domain.Enums;

/// <summary>
/// Live drift of a promoted model against the expectancy it earned in training. Unlike
/// <see cref="DriftStatus"/>, which describes day-over-day movement of a ticker inside the
/// Top 10, this is the governor's read on whether the <b>model</b> still earns its keep on the
/// scorecard. It throttles new risk; it never rewrites the published signal.
/// </summary>
public enum LiveDriftStatus
{
    /// <summary>Graded picks agree with the model's own validation record.</summary>
    STABLE,

    /// <summary>Win rate or expectancy has degraded past the noise band: small-size new risk.</summary>
    WARNING,

    /// <summary>Rolling expectancy is no longer positive: deeply reduced new risk.</summary>
    DEGRADED,

    /// <summary>Rolling expectancy is materially negative: no new risk until it recovers.</summary>
    HALTED
}
