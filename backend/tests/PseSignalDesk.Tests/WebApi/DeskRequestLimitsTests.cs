using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// The lookback ceiling is what stops one HTTP request from asking both feeds for a hundred years
/// of daily bars. The lower bound makes a caller's mistake a <c>400</c> instead of a silent lift.
/// </summary>
public sealed class DeskRequestLimitsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(420)]
    [InlineData(DeskRequestLimits.MaxLookbackCalendarDays)]
    public void Accepted_windows(int? lookbackDays) =>
        Assert.True(DeskRequestLimits.IsLookbackAllowed(lookbackDays));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(DeskRequestLimits.MaxLookbackCalendarDays + 1)]
    [InlineData(36_500)]
    [InlineData(int.MaxValue)]
    public void Rejected_windows(int? lookbackDays) =>
        Assert.False(DeskRequestLimits.IsLookbackAllowed(lookbackDays));

    [Fact]
    public void The_message_names_both_bounds()
    {
        Assert.Contains("1", DeskRequestLimits.LookbackRequirement, StringComparison.Ordinal);
        Assert.Contains("1825", DeskRequestLimits.LookbackRequirement, StringComparison.Ordinal);
        Assert.Contains("five years", DeskRequestLimits.LookbackRequirement, StringComparison.Ordinal);
    }
}
