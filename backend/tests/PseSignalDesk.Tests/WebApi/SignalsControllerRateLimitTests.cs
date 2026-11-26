using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
using PseSignalDesk.WebApi.Controllers;
using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// The four endpoints that fan out to Yahoo Finance / PSE Edge and spend Ollama Cloud credit must
/// carry the hourly ceiling. The limiter is opt-in per action, so a refactor that drops the
/// attribute removes the protection silently — this test is the tripwire.
/// </summary>
public sealed class SignalsControllerRateLimitTests
{
    [Theory]
    [InlineData(nameof(SignalsController.RunNow))]
    [InlineData(nameof(SignalsController.RunBacktest))]
    [InlineData(nameof(SignalsController.Train))]
    [InlineData(nameof(SignalsController.TrainAll))]
    public void Expensive_endpoint_carries_the_shared_rate_limit_policy(string action)
    {
        var method = typeof(SignalsController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var attribute = method.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DeskRateLimitPolicies.Expensive, attribute.PolicyName);
    }

    [Theory]
    [InlineData(nameof(SignalsController.GetLatest))]
    [InlineData(nameof(SignalsController.GetByDate))]
    [InlineData(nameof(SignalsController.GetDates))]
    [InlineData(nameof(SignalsController.GetScorecard))]
    [InlineData(nameof(SignalsController.GetDeskMemory))]
    public void Stored_reads_are_not_capped(string action)
    {
        // The terminal re-reads its stored session freely; capping those would break the desk, not
        // protect the upstream feeds.
        var method = typeof(SignalsController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Null(method.GetCustomAttribute<EnableRateLimitingAttribute>());
    }
}
