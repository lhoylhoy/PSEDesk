using PseSignalDesk.Infrastructure.MarketData;

namespace PseSignalDesk.Tests.Infrastructure;

/// <summary>
/// The cache is the only thing standing between the desk and a burst of duplicate Yahoo chart
/// calls, so its contract is pinned here: one fetch per fresh key, no cached failures, no
/// caller-cancellable download, bounded growth. No socket is opened — every factory is a stub.
/// </summary>
public sealed class MarketDataCacheTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(10);

    [Fact]
    public async Task GetOrAddAsync_shares_one_fetch_between_concurrent_callers()
    {
        var cache = new MarketDataCache();
        var calls = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> Factory()
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        var first = cache.GetOrAddAsync("chart:JFC:420", Ttl, Factory);
        var second = cache.GetOrAddAsync("chart:JFC:420", Ttl, Factory);

        gate.SetResult(7);

        Assert.Equal(7, await first);
        Assert.Equal(7, await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_serves_a_completed_entry_without_calling_the_factory_again()
    {
        var cache = new MarketDataCache();
        var calls = 0;

        Task<string> Factory()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("tape");
        }

        Assert.Equal("tape", await cache.GetOrAddAsync("page:disclosure", Ttl, Factory));
        Assert.Equal("tape", await cache.GetOrAddAsync("page:disclosure", Ttl, Factory));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_never_caches_a_failure_so_the_next_caller_retries_upstream()
    {
        var cache = new MarketDataCache();
        var calls = 0;

        Task<string> Factory() => Interlocked.Increment(ref calls) == 1
            ? Task.FromException<string>(new HttpRequestException("yahoo is down"))
            : Task.FromResult("recovered");

        await Assert.ThrowsAsync<HttpRequestException>(() => cache.GetOrAddAsync("chart:SM", Ttl, Factory));

        Assert.Equal("recovered", await cache.GetOrAddAsync("chart:SM", Ttl, Factory));
        Assert.Equal(2, calls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public async Task GetOrAddAsync_refetches_once_the_entry_is_older_than_its_ttl()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 9, 30, 0, TimeSpan.Zero));
        var cache = new MarketDataCache(clock);
        var calls = 0;

        Task<int> Factory()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(calls);
        }

        Assert.Equal(1, await cache.GetOrAddAsync("chart:BDO", TimeSpan.FromMinutes(10), Factory));

        clock.Advance(TimeSpan.FromMinutes(9));
        Assert.Equal(1, await cache.GetOrAddAsync("chart:BDO", TimeSpan.FromMinutes(10), Factory));

        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(2, await cache.GetOrAddAsync("chart:BDO", TimeSpan.FromMinutes(10), Factory));
    }

    [Fact]
    public async Task GetOrAddAsync_keeps_the_shared_fetch_running_when_a_caller_gives_up()
    {
        var cache = new MarketDataCache();
        var calls = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> Factory()
        {
            Interlocked.Increment(ref calls);
            return gate.Task;
        }

        using var abandoned = new CancellationTokenSource();
        var pending = cache.GetOrAddAsync("chart:ALI", Ttl, Factory, abandoned.Token);

        await abandoned.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The download the abandoned caller started is still there for the next one.
        gate.SetResult(11);

        Assert.Equal(11, await cache.GetOrAddAsync("chart:ALI", Ttl, Factory));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_does_not_start_a_fetch_for_an_already_cancelled_caller()
    {
        var cache = new MarketDataCache();
        var calls = 0;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrAddAsync(
            "chart:TEL",
            Ttl,
            () =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(1);
            },
            cancelled.Token));

        Assert.Equal(0, calls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task Clear_forces_the_next_caller_to_fetch_again()
    {
        var cache = new MarketDataCache();
        var calls = 0;

        Task<string> Factory()
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult("bar");
        }

        Assert.Equal("bar", await cache.GetOrAddAsync("chart:JFC", Ttl, Factory));
        cache.Clear();

        Assert.Equal(0, cache.Count);
        Assert.Equal("bar", await cache.GetOrAddAsync("chart:JFC", Ttl, Factory));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrAddAsync_evicts_the_oldest_entry_when_the_map_is_full()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 26, 9, 30, 0, TimeSpan.Zero));
        var cache = new MarketDataCache(clock, maxEntries: 2);
        var fetched = new List<string>();

        Task<string> Factory(string symbol)
        {
            fetched.Add(symbol);
            return Task.FromResult(symbol);
        }

        foreach (var symbol in new[] { "AC", "ALI", "BDO" })
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await cache.GetOrAddAsync($"chart:{symbol}", Ttl, () => Factory(symbol));
        }

        Assert.Equal(2, cache.Count);

        // "AC" was the oldest, so the bound dropped it and the next call refetches it.
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("AC", await cache.GetOrAddAsync("chart:AC", Ttl, () => Factory("AC")));
        Assert.Equal(new[] { "AC", "ALI", "BDO", "AC" }, fetched);
    }

    /// <summary>A clock the test moves by hand, so a TTL boundary is asserted without a sleep.</summary>
    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}