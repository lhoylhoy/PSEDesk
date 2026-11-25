using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PseSignalDesk.Infrastructure.Configuration;
using PseSignalDesk.WebApi.Middleware;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// The compliance guard is the deployment-level answer to two risks this desk cannot fix in code
/// alone: the scraped Yahoo Finance / PSE Edge feed may not be redistributed off the operator's
/// machine, and a multi-user deployment must not accept unauthenticated writes.
/// </summary>
public sealed class ComplianceGuardMiddlewareTests
{
    private static readonly IPAddress LoopbackV4 = IPAddress.Loopback;
    private static readonly IPAddress RemoteV4 = IPAddress.Parse("203.0.113.7");

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task InvokeAsync_refuses_a_non_loopback_caller_in_personal_local_mode(string method)
    {
        var called = false;
        var context = CreateContext(method, RemoteV4);
        var middleware = CreateMiddleware(PersonalLocal(), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("application/problem+json", context.Response.ContentType);

        var body = await ReadBodyAsync(context);
        Assert.Contains("PersonalLocal", body, StringComparison.Ordinal);
        Assert.Contains("redistribut", body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("127.0.0.1")]
    public async Task InvokeAsync_allows_a_loopback_caller_in_personal_local_mode(string address)
    {
        var called = false;
        var context = CreateContext("GET", IPAddress.Parse(address));
        var middleware = CreateMiddleware(PersonalLocal(), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_treats_a_null_remote_address_as_local()
    {
        // The in-memory test host never opens a socket, so there is no address to judge.
        var called = false;
        var context = CreateContext("GET", remoteAddress: null);
        var middleware = CreateMiddleware(PersonalLocal(), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Fact]
    public async Task InvokeAsync_lets_health_probes_through_from_any_address()
    {
        var called = false;
        var context = CreateContext("GET", RemoteV4, path: "/health");
        var middleware = CreateMiddleware(PersonalLocal(), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_allows_a_remote_caller_when_the_loopback_rule_is_off()
    {
        var called = false;
        var options = PersonalLocal();
        options.EnforceLoopbackInPersonalMode = false;

        var context = CreateContext("GET", RemoteV4);
        var middleware = CreateMiddleware(options, _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task InvokeAsync_does_not_require_the_operator_key_for_reads(string method)
    {
        var called = false;
        var context = CreateContext(method, RemoteV4);
        var middleware = CreateMiddleware(LicensedWithKey("s3cret-key"), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public async Task InvokeAsync_answers_401_when_a_write_lacks_the_operator_key(string method)
    {
        var called = false;
        var context = CreateContext(method, RemoteV4);
        var middleware = CreateMiddleware(LicensedWithKey("s3cret-key"), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_answers_401_when_the_operator_key_is_wrong()
    {
        var called = false;
        var context = CreateContext("POST", RemoteV4);
        context.Request.Headers[ComplianceOptions.OperatorKeyHeaderName] = "s3cret-kex";
        var middleware = CreateMiddleware(LicensedWithKey("s3cret-key"), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_accepts_a_write_that_presents_the_matching_operator_key()
    {
        var called = false;
        var context = CreateContext("POST", RemoteV4);
        context.Request.Headers[ComplianceOptions.OperatorKeyHeaderName] = "  s3cret-key  ";
        var middleware = CreateMiddleware(LicensedWithKey("s3cret-key"), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.True(called);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_answers_503_when_a_licensed_deployment_has_no_key_at_all()
    {
        // Failing closed: an unconfigured multi-user deployment must not silently expose writes.
        var called = false;
        var options = new ComplianceOptions
        {
            DeploymentMode = ComplianceOptions.LicensedCommercialMode,
            EnforceLoopbackInPersonalMode = false,
            OperatorApiKey = "   "
        };

        var context = CreateContext("POST", RemoteV4);
        var middleware = CreateMiddleware(options, _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.False(called);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Contains("PSEDESK_OPERATOR_API_KEY", await ReadBodyAsync(context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_keeps_personal_mode_writes_working_without_a_key_on_loopback()
    {
        // The single-user desk has no login: on the operator's own machine writes stay open.
        var called = false;
        var context = CreateContext("POST", LoopbackV4);
        var middleware = CreateMiddleware(PersonalLocal(), _ => called = true);

        await middleware.InvokeAsync(context);

        Assert.True(called);
    }

    private static ComplianceOptions PersonalLocal() => new()
    {
        DeploymentMode = ComplianceOptions.PersonalLocalMode,
        EnforceLoopbackInPersonalMode = true,
        OperatorApiKey = string.Empty
    };

    private static ComplianceOptions LicensedWithKey(string key) => new()
    {
        DeploymentMode = ComplianceOptions.LicensedCommercialMode,
        EnforceLoopbackInPersonalMode = false,
        OperatorApiKey = key
    };

    private static ComplianceGuardMiddleware CreateMiddleware(ComplianceOptions options, Action<HttpContext> next) =>
        new(
            context =>
            {
                next(context);
                return Task.CompletedTask;
            },
            Options.Create(options),
            NullLogger<ComplianceGuardMiddleware>.Instance);

    private static DefaultHttpContext CreateContext(
        string method,
        IPAddress? remoteAddress,
        string path = "/api/signals/latest")
    {
        var context = new DefaultHttpContext
        {
            Request =
            {
                Method = method,
                Path = path
            }
        };

        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = remoteAddress;
        context.Connection.RemotePort = remoteAddress is null ? 0 : 51_234;

        return context;
    }

    private static async Task<string> ReadBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);

        return await reader.ReadToEndAsync();
    }
}
