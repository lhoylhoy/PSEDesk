using System.Collections.Concurrent;

namespace PseSignalDesk.WebApi.Services;

/// <summary>
/// A small per-caller failure window in front of the operator key. The key is compared in constant
/// time, but a constant-time comparison does not make guessing expensive: without this, a caller
/// who can reach a <c>LicensedCommercial</c> deployment could try keys as fast as the network
/// allows. Five wrong keys in five minutes answers <c>429</c> with <c>Retry-After</c> until the
/// window rolls over, and a correct key clears the caller's record.
/// </summary>
/// <remarks>
/// The state is in memory on purpose: this protects a single desk process, and a restart clearing
/// the counters is preferable to a persisted lockout that could strand the operator. The map is
/// pruned once it grows past <see cref="MaxTrackedCallers"/> so a spray of source addresses cannot
/// grow it without bound.
/// </remarks>
public sealed class OperatorKeyThrottle
{
    /// <summary>Wrong keys tolerated in one window before the caller is locked out.</summary>
    public const int MaxFailuresPerWindow = 5;

    /// <summary>How long a failure (and a lockout) lasts.</summary>
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);

    private const int MaxTrackedCallers = 1024;

    private readonly ConcurrentDictionary<string, AttemptWindow> _windows = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public OperatorKeyThrottle(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>
    /// True when <paramref name="caller"/> has exhausted its window. <paramref name="retryAfter"/>
    /// is the remaining lockout, suitable for a <c>Retry-After</c> header.
    /// </summary>
    public bool IsBlocked(string caller, out TimeSpan retryAfter)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        retryAfter = TimeSpan.Zero;

        if (!_windows.TryGetValue(caller, out var window))
        {
            return false;
        }

        var now = _time.GetUtcNow();

        lock (window)
        {
            var elapsed = now - window.StartedAt;

            if (elapsed >= FailureWindow)
            {
                window.StartedAt = now;
                window.Failures = 0;
                return false;
            }

            if (window.Failures < MaxFailuresPerWindow)
            {
                return false;
            }

            retryAfter = FailureWindow - elapsed;
            return retryAfter > TimeSpan.Zero;
        }
    }

    /// <summary>Records one wrong key for <paramref name="caller"/>.</summary>
    public void RecordFailure(string caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);

        var now = _time.GetUtcNow();
        var window = _windows.GetOrAdd(caller, _ => new AttemptWindow { StartedAt = now });

        lock (window)
        {
            if (now - window.StartedAt >= FailureWindow)
            {
                window.StartedAt = now;
                window.Failures = 0;
            }

            window.Failures++;
        }

        Prune(now);
    }

    /// <summary>Forgets a caller's failures after a correct key.</summary>
    public void Reset(string caller)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(caller);
        _windows.TryRemove(caller, out _);
    }

    private void Prune(DateTimeOffset now)
    {
        if (_windows.Count <= MaxTrackedCallers)
        {
            return;
        }

        foreach (var pair in _windows)
        {
            lock (pair.Value)
            {
                if (now - pair.Value.StartedAt >= FailureWindow)
                {
                    _windows.TryRemove(pair.Key, out _);
                }
            }
        }
    }

    private sealed class AttemptWindow
    {
        public DateTimeOffset StartedAt { get; set; }

        public int Failures { get; set; }
    }
}
