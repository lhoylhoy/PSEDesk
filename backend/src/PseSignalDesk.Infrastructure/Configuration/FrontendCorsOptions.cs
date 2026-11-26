namespace PseSignalDesk.Infrastructure.Configuration;

/// <summary>
/// The browser origins allowed to call the API. <c>Program.cs</c> builds the CORS policy from this
/// list and <c>ComplianceGuardMiddleware</c> uses the same list to refuse cross-site writes, so an
/// origin that may read a response is exactly an origin that may change state — and one that may
/// not read cannot write either.
/// </summary>
public sealed class FrontendCorsOptions
{
    public const string SectionName = "Cors";

    /// <summary>The Vite dev server the terminal ships with.</summary>
    public const string DefaultOrigin = "http://localhost:5173";

    /// <summary>Absolute origins, scheme and authority only; paths are ignored.</summary>
    public string[] AllowedOrigins { get; set; } = { DefaultOrigin };

    /// <summary>
    /// True when <paramref name="origin"/> normalises to one of the configured origins. A missing,
    /// relative, or opaque (<c>null</c>) origin is never allowed: those are the shapes a sandboxed
    /// iframe or a <c>file://</c> page sends.
    /// </summary>
    public bool IsAllowed(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin) ||
            origin.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(origin, UriKind.Absolute, out var candidate))
        {
            return false;
        }

        var candidateAuthority = candidate.GetLeftPart(UriPartial.Authority);

        foreach (var configured in AllowedOrigins)
        {
            if (Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var allowed) &&
                string.Equals(
                    candidateAuthority,
                    allowed.GetLeftPart(UriPartial.Authority),
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
