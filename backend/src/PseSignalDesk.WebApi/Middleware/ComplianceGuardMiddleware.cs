using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using PseSignalDesk.Infrastructure.Configuration;

namespace PseSignalDesk.WebApi.Middleware;

/// <summary>
/// Two statutory/contractual guardrails that run before any controller:
/// <list type="number">
/// <item>
/// <b>Personal-use market data.</b> Yahoo Finance charts and PSE Edge pages are consumed under a
/// personal, non-commercial research posture. In <c>PersonalLocal</c> mode every non-loopback
/// request is refused with <c>403</c>, so scraped prices and filings cannot be redistributed over a
/// LAN or a public address without a PSE Market Data licence.
/// </item>
/// <item>
/// <b>Operator key.</b> When <c>Compliance:OperatorApiKey</c> is set, every mutating request
/// (<c>POST</c>/<c>PUT</c>/<c>PATCH</c>/<c>DELETE</c>) must present the matching
/// <c>X-PseDesk-Key</c> header, compared in constant time. A <c>LicensedCommercial</c> deployment
/// without a key is misconfigured and answers <c>503</c> instead of exposing writes.
/// </item>
/// </list>
/// <c>/health</c> stays reachable so container and process probes keep working.
/// </summary>
public sealed class ComplianceGuardMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ComplianceOptions _options;
    private readonly ILogger<ComplianceGuardMiddleware> _logger;

    public ComplianceGuardMiddleware(
        RequestDelegate next,
        IOptions<ComplianceOptions> options,
        ILogger<ComplianceGuardMiddleware> logger)
    {
        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Liveness probes are local infrastructure, not a market-data read.
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (_options.IsPersonalLocal &&
            _options.EnforceLoopbackInPersonalMode &&
            !IsLoopback(context.Connection.RemoteIpAddress))
        {
            _logger.LogWarning(
                "Refused {Method} {Path} from {RemoteIp}: the desk is in PersonalLocal mode and market data may not be redistributed off this machine.",
                context.Request.Method,
                context.Request.Path,
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            await WriteProblemAsync(
                context,
                StatusCodes.Status403Forbidden,
                "Personal, non-commercial research only",
                "This desk is running in PersonalLocal mode. Its market data comes from Yahoo Finance " +
                "and PSE Edge under a personal research posture, so it may not be served to another " +
                "machine, a LAN or a public address. Redistributing PSE market data requires a PSE " +
                "Market Data Vendor/redistribution agreement; to host for other users, set " +
                "Compliance:DeploymentMode to LicensedCommercial with Compliance:OperatorApiKey " +
                "after obtaining that licence.");

            return;
        }

        if (IsMutating(context.Request.Method))
        {
            var configuredKey = _options.OperatorApiKey?.Trim() ?? string.Empty;

            if (configuredKey.Length > 0)
            {
                var suppliedKey = context.Request.Headers[ComplianceOptions.OperatorKeyHeaderName].ToString();

                if (!Matches(suppliedKey, configuredKey))
                {
                    _logger.LogWarning(
                        "Refused {Method} {Path}: missing or invalid {Header}.",
                        context.Request.Method,
                        context.Request.Path,
                        ComplianceOptions.OperatorKeyHeaderName);

                    await WriteProblemAsync(
                        context,
                        StatusCodes.Status401Unauthorized,
                        "Operator key required",
                        "This deployment accepts state-changing requests only with a valid " +
                        $"{ComplianceOptions.OperatorKeyHeaderName} header.");

                    return;
                }
            }
            else if (_options.IsLicensedCommercial)
            {
                _logger.LogError(
                    "Refused {Method} {Path}: LicensedCommercial mode has no Compliance:OperatorApiKey configured.",
                    context.Request.Method,
                    context.Request.Path);

                await WriteProblemAsync(
                    context,
                    StatusCodes.Status503ServiceUnavailable,
                    "Operator key not configured",
                    "This desk is configured as a licensed multi-user deployment but no " +
                    "Compliance:OperatorApiKey is set, so state-changing endpoints are disabled. " +
                    "Set PSEDESK_OPERATOR_API_KEY before serving writes.");

                return;
            }
        }

        await _next(context);
    }

    /// <summary>
    /// A null address means the request never came through a socket (the in-memory test host, and
    /// the ASP.NET Core test server), which is by definition local.
    /// </summary>
    private static bool IsLoopback(IPAddress? address) => address is null || IPAddress.IsLoopback(address);

    private static bool IsMutating(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) ||
        HttpMethods.IsDelete(method);

    /// <summary>Length-safe, constant-time comparison so a wrong key cannot be timed out byte by byte.</summary>
    private static bool Matches(string supplied, string expected)
    {
        if (string.IsNullOrEmpty(supplied))
        {
            return false;
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied.Trim());
        var expectedBytes = Encoding.UTF8.GetBytes(expected);

        return suppliedBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
    }

    private static async Task WriteProblemAsync(HttpContext context, int statusCode, string title, string detail)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(
            new ProblemDetails
            {
                Status = statusCode,
                Title = title,
                Detail = detail
            },
            options: (System.Text.Json.JsonSerializerOptions?)null,
            contentType: "application/problem+json");
    }
}
