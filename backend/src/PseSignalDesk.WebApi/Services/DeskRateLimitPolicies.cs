namespace PseSignalDesk.WebApi.Services;

/// <summary>
/// Names of the rate-limit policies this API registers, shared by <c>Program.cs</c> (which defines
/// them) and the controllers (which opt in with <c>[EnableRateLimiting]</c>), so a rename cannot
/// leave an endpoint silently unlimited.
/// </summary>
public static class DeskRateLimitPolicies
{
    /// <summary>
    /// The endpoints that fan out to Yahoo Finance / PSE Edge and spend Ollama Cloud credit: the
    /// live run, the causal replay and the money-manager training jobs. A human drives these a
    /// handful of times a day; a loop drives them thousands of times an hour, which is what the
    /// polite-scraping and no-unbounded-spend promises in <c>THIRD_PARTY_NOTICES.md</c> preclude.
    /// </summary>
    public const string Expensive = "desk-expensive";
}
