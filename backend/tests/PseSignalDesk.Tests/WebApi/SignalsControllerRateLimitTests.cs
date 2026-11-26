using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
using PseSignalDesk.WebApi.Controllers;
using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// Every endpoint that fans out to an upstream feed or spends model credit must carry its hourly
/// policy. The limiter is opt-in per action, so a refactor that drops the attribute removes the
/// protection silently — these tests are the tripwire.
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

    [Fact]
    public void Research_lookup_carries_the_upstream_policy()
    {
        var method = typeof(ResearchController).GetMethod(
            nameof(ResearchController.GetResearch),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var attribute = method.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DeskRateLimitPolicies.Upstream, attribute.PolicyName);
    }

    [Theory]
    [InlineData(nameof(KnowledgeController.Reindex))]
    [InlineData(nameof(KnowledgeController.Upload))]
    public void Knowledge_ingestion_carries_the_model_policy(string action)
    {
        var method = typeof(KnowledgeController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var attribute = method.GetCustomAttribute<EnableRateLimitingAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DeskRateLimitPolicies.Model, attribute.PolicyName);
    }
}
