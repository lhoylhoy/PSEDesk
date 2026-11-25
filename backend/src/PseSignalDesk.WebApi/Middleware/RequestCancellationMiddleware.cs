namespace PseSignalDesk.WebApi.Middleware;

/// <summary>
/// Answers a request the client abandoned with a quiet 499 instead of letting the
/// cancellation escape as an unhandled <see cref="OperationCanceledException"/>.
/// Browsers abort constantly: React StrictMode remounts every screen once (the cleanup
/// cancels the first fetch), switching date or horizon cancels the in-flight load, and
/// closing the tab cancels a running pipeline. Kestrel reports that through
/// <see cref="HttpContext.RequestAborted"/>, and EF Core surfaces it as a
/// <see cref="TaskCanceledException"/> while opening the SQLite connection. Nobody is
/// listening for the response, so it must not be logged as a server fault or turned into
/// a 500 (or an "unhandled exception" break while debugging).
/// </summary>
public sealed class RequestCancellationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestCancellationMiddleware> _logger;

    public RequestCancellationMiddleware(RequestDelegate next, ILogger<RequestCancellationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // One catch covers both shapes: TaskCanceledException (EF Core, HttpClient)
            // derives from OperationCanceledException.
            _logger.LogDebug(
                "{Method} {Path} was abandoned by the client before it completed.",
                context.Request.Method,
                context.Request.Path);

            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status499ClientClosedRequest;
            }
        }
    }
}
