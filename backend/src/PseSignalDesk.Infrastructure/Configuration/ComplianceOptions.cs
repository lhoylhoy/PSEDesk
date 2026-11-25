namespace PseSignalDesk.Infrastructure.Configuration;

/// <summary>
/// Deployment-scope guardrails. The desk consumes Yahoo Finance charts and PSE Edge pages, which
/// are strictly personal, non-commercial research feeds: redistributing that data to other people
/// needs a PSE Market Data Vendor / redistribution agreement that this repository does not have.
/// Because the deployment plan is still undecided, the default posture is
/// <see cref="PersonalLocalMode"/> with loopback-only access, so scraped market data can never be
/// served over a LAN or a public IP by accident.
/// </summary>
public sealed class ComplianceOptions
{
    public const string SectionName = "Compliance";

    /// <summary>Single-operator, loopback-only desk running on the operator's own machine.</summary>
    public const string PersonalLocalMode = "PersonalLocal";

    /// <summary>
    /// Multi-user hosting. Selecting it is an assertion that the operator holds a PSE market-data
    /// licence (and any SEC registration the activity needs); it is never inferred.
    /// </summary>
    public const string LicensedCommercialMode = "LicensedCommercial";

    /// <summary>Header a licensed deployment (or a keyed local one) must present on writes.</summary>
    public const string OperatorKeyHeaderName = "X-PseDesk-Key";

    /// <summary>
    /// <c>PersonalLocal</c> (default) or <c>LicensedCommercial</c>. Case-insensitive; anything
    /// unrecognised is treated as personal, because the safe default must not be a typo away
    /// from an unlicensed public deployment.
    /// </summary>
    public string DeploymentMode { get; set; } = PersonalLocalMode;

    /// <summary>
    /// When true (default in personal mode) every non-loopback request is refused with
    /// <c>403</c>, so the API cannot redistribute Yahoo Finance or PSE Edge content over a
    /// network without an explicit decision.
    /// </summary>
    public bool EnforceLoopbackInPersonalMode { get; set; } = true;

    /// <summary>
    /// Optional shared operator key. When set, mutating requests must carry
    /// <see cref="OperatorKeyHeaderName"/>. Blank (the default) keeps the local desk
    /// friction-free; the value is meant to be injected through
    /// <c>PSEDESK_OPERATOR_API_KEY</c>, never committed.
    /// </summary>
    public string OperatorApiKey { get; set; } = string.Empty;

    /// <summary>True when the desk is running as the single-operator local research desk.</summary>
    public bool IsPersonalLocal =>
        !string.Equals(DeploymentMode, LicensedCommercialMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>True only when the operator explicitly declared a licensed deployment.</summary>
    public bool IsLicensedCommercial => !IsPersonalLocal;
}
