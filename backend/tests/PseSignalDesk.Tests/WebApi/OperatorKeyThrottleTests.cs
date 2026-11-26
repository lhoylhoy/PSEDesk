using PseSignalDesk.WebApi.Services;

namespace PseSignalDesk.Tests.WebApi;

/// <summary>
/// The operator-key lockout is the difference between a constant-time comparison and a guessing
/// oracle an attacker can query for free. These tests pin the window, the threshold, the reset and
/// the caller isolation without waiting five real minutes.
/// </summary>
public sealed class OperatorKeyThrottleTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 26, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_few_wrong_keys_do_not_lock_the_caller()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = new OperatorKeyThrottle(time);

        for (var attempt = 1; attempt < OperatorKeyThrottle.MaxFailuresPerWindow; attempt++)
        {
            throttle.RecordFailure("203.0.113.7");
        }

        Assert.False(throttle.IsBlocked("203.0.113.7", out var retryAfter));
        Assert.Equal(TimeSpan.Zero, retryAfter);
    }

    [Fact]
    public void The_last_tolerated_wrong_key_locks_the_caller()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = new OperatorKeyThrottle(time);

        for (var attempt = 0; attempt < OperatorKeyThrottle.MaxFailuresPerWindow; attempt++)
        {
            throttle.RecordFailure("203.0.113.7");
        }

        Assert.True(throttle.IsBlocked("203.0.113.7", out var retryAfter));
        Assert.Equal(OperatorKeyThrottle.FailureWindow, retryAfter);
    }

    [Fact]
    public void The_lock_shrinks_as_the_window_rolls_over_and_then_expires()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = new OperatorKeyThrottle(time);

        for (var attempt = 0; attempt < OperatorKeyThrottle.MaxFailuresPerWindow; attempt++)
        {
            throttle.RecordFailure("203.0.113.7");
        }

        time.Advance(TimeSpan.FromMinutes(4));

        Assert.True(throttle.IsBlocked("203.0.113.7", out var retryAfter));
        Assert.Equal(TimeSpan.FromMinutes(1), retryAfter);

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.False(throttle.IsBlocked("203.0.113.7", out _));
    }

    [Fact]
    public void A_correct_key_clears_the_record()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = new OperatorKeyThrottle(time);

        for (var attempt = 0; attempt < OperatorKeyThrottle.MaxFailuresPerWindow; attempt++)
        {
            throttle.RecordFailure("203.0.113.7");
        }

        throttle.Reset("203.0.113.7");

        Assert.False(throttle.IsBlocked("203.0.113.7", out _));

        throttle.RecordFailure("203.0.113.7");

        Assert.False(throttle.IsBlocked("203.0.113.7", out _));
    }

    [Fact]
    public void One_caller_cannot_lock_out_another()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = new OperatorKeyThrottle(time);

        for (var attempt = 0; attempt < OperatorKeyThrottle.MaxFailuresPerWindow; attempt++)
        {
            throttle.RecordFailure("203.0.113.7");
        }

        Assert.True(throttle.IsBlocked("203.0.113.7", out _));
        Assert.False(throttle.IsBlocked("203.0.113.8", out _));
    }

    [Fact]
    public void A_new_window_starts_clean_after_an_expired_lockout()
    {
        var time = new FakeTimeProvider(Start);
        var throttle = new OperatorKeyThrottle(time);

        for (var attempt = 0; attempt < OperatorKeyThrottle.MaxFailuresPerWindow; attempt++)
        {
            throttle.RecordFailure("203.0.113.7");
        }

        time.Advance(OperatorKeyThrottle.FailureWindow + TimeSpan.FromSeconds(1));

        Assert.False(throttle.IsBlocked("203.0.113.7", out _));

        throttle.RecordFailure("203.0.113.7");

        Assert.False(throttle.IsBlocked("203.0.113.7", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_caller_is_a_programming_error(string caller)
    {
        var throttle = new OperatorKeyThrottle(new FakeTimeProvider(Start));

        Assert.Throws<ArgumentException>(() => throttle.IsBlocked(caller, out _));
        Assert.Throws<ArgumentException>(() => throttle.RecordFailure(caller));
        Assert.Throws<ArgumentException>(() => throttle.Reset(caller));
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }
}
