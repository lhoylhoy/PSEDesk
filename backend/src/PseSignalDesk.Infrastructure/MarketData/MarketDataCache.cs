using System.Collections.Concurrent;

namespace PseSignalDesk.Infrastructure.MarketData;

/// <summary>
/// A small, bounded, thread-safe cache of upstream market-data work, keyed by the thing actually
/// downloaded: one Yahoo chart URL (which already carries the symbol and the resolved date window)
/// and one PSE Edge page URL.
/// <para>
/// Three properties matter and each is deliberate:
/// <list type="bullet">
/// <item>The <b>task</b> is cached, not the value, so concurrent identical requests share one
/// HTTP call. The first caller inserts an empty entry and starts the fetch; every other caller
/// awaits the same task.</item>
/// <item>A failure or cancellation is <b>never served</b>: the entry is evicted before the
/// exception reaches the caller, so the next request retries upstream.</item>
/// <item>A caller that abandons its request does not cancel the shared fetch. The factory runs
/// without the caller's token (the provider still applies its own per-request timeout) and each
/// caller waits with <see cref="Task.WaitAsync(CancellationToken)"/>, so one closed tab cannot
/// poison the download another request is waiting for.</item>
/// </list>
/// </para>
/// <para>
/// Entries are replaced on access once they are older than their TTL; there is no background
/// timer. The map is bounded so a long-lived process cannot grow it without limit.
/// </para>
/// </summary>
public sealed class MarketDataCache
{
    public const int DefaultMaxEntries = 200;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly int _maxEntries;

    public MarketDataCache(TimeProvider? time = null, int maxEntries = DefaultMaxEntries)
    {
        _time = time ?? TimeProvider.System;
        _maxEntries = Math.Max(1, maxEntries);
    }

    /// <summary>How many entries the map currently holds. Exposed for the cache tests and diagnostics.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, starting <paramref name="factory"/>
    /// when there is no fresh entry. The factory is called at most once per fresh entry, whatever
    /// the number of concurrent callers.
    /// </summary>
    public async Task<T> GetOrAddAsync<T>(
        string key,
        TimeSpan ttl,
        Func<Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();

        var entry = Resolve(key, ttl, factory);

        // The shared fetch is not tied to this caller: abandoning the wait leaves it running so
        // the next caller can consume it (or, if it fails, retry it).
        var value = await entry.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        return value is null ? default! : (T)value;
    }

    /// <summary>Drops every entry. A manual run calls this so it always sees the newest tape.</summary>
    public void Clear() => _entries.Clear();

    private Entry Resolve<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        var now = _time.GetUtcNow();

        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (IsFresh(existing, now, ttl))
                {
                    return existing;
                }

                var replacement = new Entry(now);

                if (_entries.TryUpdate(key, replacement, existing))
                {
                    Start(key, replacement, factory);
                    return replacement;
                }

                // Another caller replaced it first; use whatever is there now.
                continue;
            }

            var created = new Entry(now);

            if (_entries.TryAdd(key, created))
            {
                Start(key, created, factory);
                EvictOldest();
                return created;
            }
        }
    }

    /// <summary>Starts the shared fetch for the entry this caller won, capturing every outcome in its task.</summary>
    private void Start<T>(string key, Entry entry, Func<Task<T>> factory) =>
        entry.Start(async () =>
        {
            try
            {
                return await factory().ConfigureAwait(false);
            }
            catch
            {
                // Failures are never served: only the exact entry that failed is removed, so a
                // newer value another caller published is left alone.
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                throw;
            }
        });

    private static bool IsFresh(Entry entry, DateTimeOffset now, TimeSpan ttl) =>
        now - entry.CreatedAt < ttl && !entry.Task.IsFaulted && !entry.Task.IsCanceled;

    private void EvictOldest()
    {
        if (_entries.Count <= _maxEntries)
        {
            return;
        }

        var excess = _entries.Count - _maxEntries;

        // A concurrent Clear() can hand this enumeration a node whose value is already gone, so a
        // missing value is skipped instead of dereferenced. Eviction is best-effort: the next
        // insert trims the map again.
        var oldest = _entries
            .Where(pair => pair.Value is not null)
            .OrderBy(pair => pair.Value.CreatedAt)
            .Take(excess)
            .ToList();

        foreach (var pair in oldest)
        {
            _entries.TryRemove(pair.Key, out _);
        }
    }

    /// <summary>
    /// One cached fetch. Its completion source is published before the factory starts, so a
    /// caller that finds the entry can await it without ever observing a half-built task.
    /// </summary>
    private sealed class Entry
    {
        private readonly TaskCompletionSource<object?> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Entry(DateTimeOffset createdAt) => CreatedAt = createdAt;

        public DateTimeOffset CreatedAt { get; }

        public Task<object?> Task => _completion.Task;

        public void Start(Func<Task<object?>> run) => _ = RunAsync(run);

        private async Task RunAsync(Func<Task<object?>> run)
        {
            try
            {
                _completion.TrySetResult(await run().ConfigureAwait(false));
            }
            catch (OperationCanceledException ex)
            {
                _completion.TrySetCanceled(ex.CancellationToken);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }
}
