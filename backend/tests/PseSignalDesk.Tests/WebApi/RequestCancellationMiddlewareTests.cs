using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using PseSignalDesk.WebApi.Middleware;

namespace PseSignalDesk.Tests.WebApi;

public sealed class RequestCancellationMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_answers_499_when_the_client_aborted_the_request()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var context = CreateContext(aborted.Token);
        var middleware = CreateMiddleware(_ => throw new TaskCanceledException("A task was canceled."));

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_rethrows_cancellations_that_are_not_client_aborts()
    {
        var context = CreateContext(CancellationToken.None);
        var middleware = CreateMiddleware(_ => throw new OperationCanceledException("The upstream call timed out."));

        await Assert.ThrowsAsync<OperationCanceledException>(() => middleware.InvokeAsync(context));
    }

    [Fact]
    public async Task InvokeAsync_leaves_completed_requests_untouched()
    {
        var context = CreateContext(CancellationToken.None);
        var middleware = CreateMiddleware(httpContext =>
        {
            httpContext.Response.StatusCode = StatusCodes.Status200OK;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    [Fact]
    public async Task InvokeAsync_does_not_rewrite_a_response_that_already_started()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();

        var context = CreateContext(aborted.Token, responseStarted: true);
        var middleware = CreateMiddleware(_ => throw new TaskCanceledException());

        await middleware.InvokeAsync(context);

        Assert.True(context.Response.HasStarted);
        Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
    }

    private static RequestCancellationMiddleware CreateMiddleware(RequestDelegate next) =>
        new(next, NullLogger<RequestCancellationMiddleware>.Instance);

    private static DefaultHttpContext CreateContext(CancellationToken requestAborted, bool responseStarted = false)
    {
        var context = new DefaultHttpContext { RequestAborted = requestAborted };

        if (responseStarted)
        {
            // DefaultHttpContext reports HasStarted from the response feature, so swapping in
            // a started feature is the cheapest faithful simulation of a flushed response.
            context.Features.Set<IHttpResponseFeature>(new StartedResponseFeature());
        }

        return context;
    }

    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; } = StatusCodes.Status200OK;

        public string? ReasonPhrase { get; set; }

        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();

        public Stream Body { get; set; } = Stream.Null;

        public bool HasStarted => true;

        public void OnStarting(Func<object, Task> callback, object state)
        {
        }

        public void OnCompleted(Func<object, Task> callback, object state)
        {
        }
    }
}
