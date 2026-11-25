#!/usr/bin/env node
// Query cache: the terminal reads the same server payload from many places (header, table,
// dialogs, hover prefetches). This suite locks the contract that keeps those reads cheap and
// honest — verbatim payloads, stale-while-revalidate, one shared request per key, and an
// upstream abort only when the last subscriber leaves. Run with `npm test` (no network).
import assert from 'node:assert/strict';
import test from 'node:test';
import {
  clearQueryCache,
  fetchQuery,
  getSnapshot,
  invalidateQueries,
  isQueryFresh,
  prefetchQuery,
  queryKeys,
  queryStaleTime,
  setQueryData,
  subscribe,
} from '../queryCache.ts';

function isAbortError(error: unknown): boolean {
  return error instanceof Error && error.name === 'AbortError';
}

test('fresh cache hits return the verbatim payload without touching the API', async () => {
  clearQueryCache();
  const key = queryKeys.portfolioSummary;
  const payload = { totalEquityPhp: 250_000, openPositions: 2 };
  setQueryData(key, payload, queryStaleTime.portfolio);

  let calls = 0;
  const result = await fetchQuery({
    key,
    staleTimeMs: queryStaleTime.portfolio,
    fetcher: async () => {
      calls += 1;
      return { totalEquityPhp: 999_999, openPositions: 0 };
    },
  });

  assert.equal(calls, 0);
  assert.equal(result, payload, 'the exact object the server returned is what callers see');
  assert.equal(getSnapshot(key), payload);
  assert.equal(isQueryFresh(key), true);
});

test('stale entries revalidate into the cache', async () => {
  clearQueryCache();
  const key = queryKeys.signalsLatest('DAY');
  setQueryData(key, { run: null }, queryStaleTime.signals);
  invalidateQueries('signals:');
  assert.equal(isQueryFresh(key), false);

  let calls = 0;
  const fresh = { run: { runId: 'run-42' } };
  const result = await fetchQuery({
    key,
    staleTimeMs: queryStaleTime.signals,
    fetcher: async () => {
      calls += 1;
      return fresh;
    },
  });

  assert.equal(calls, 1);
  assert.equal(result, fresh);
  assert.equal(getSnapshot(key), fresh);
  assert.equal(isQueryFresh(key), true);
});

test('concurrent callers share a single upstream request', async () => {
  clearQueryCache();
  let calls = 0;
  let release: (value: string) => void = () => {};
  const pending = new Promise<string>((resolve) => {
    release = resolve;
  });
  const fetcher = (): Promise<string> => {
    calls += 1;
    return pending;
  };

  const first = fetchQuery({ key: 'shared', fetcher, staleTimeMs: 1_000 });
  const second = fetchQuery({ key: 'shared', fetcher, staleTimeMs: 1_000 });
  assert.equal(calls, 1, 'the second caller joins the in-flight request');

  release('server-payload');

  assert.equal(await first, 'server-payload');
  assert.equal(await second, 'server-payload');
  assert.equal(calls, 1);
});



test('aborting one subscriber leaves the shared request running for the rest', async () => {
  clearQueryCache();
  let calls = 0;
  let upstream: AbortSignal | null = null;
  let release: (value: string) => void = () => {};
  const pending = new Promise<string>((resolve) => {
    release = resolve;
  });
  const fetcher = (signal: AbortSignal) => {
    calls += 1;
    upstream = signal;
    return pending;
  };

  const leaving = new AbortController();
  const staying = new AbortController();
  const first = fetchQuery({ key: 'refcount', fetcher, staleTimeMs: 1_000, signal: leaving.signal });
  const second = fetchQuery({ key: 'refcount', fetcher, staleTimeMs: 1_000, signal: staying.signal });

  leaving.abort();
  await assert.rejects(first, isAbortError);
  assert.equal(upstream?.aborted, false, 'the upstream stays alive for the remaining caller');

  release('still-here');
  assert.equal(await second, 'still-here');
  assert.equal(calls, 1);
  assert.equal(upstream?.aborted, false);
});

test('aborting every subscriber cancels upstream and evicts the dead promise', async () => {
  clearQueryCache();
  let calls = 0;
  let upstream: AbortSignal | null = null;
  const fetcher = (signal: AbortSignal): Promise<string> => {
    calls += 1;
    upstream = signal;
    return new Promise<string>((_resolve, reject) => {
      signal.addEventListener('abort', () => reject(new Error('aborted by client')));
    });
  };

  const first = new AbortController();
  const second = new AbortController();
  const a = fetchQuery({ key: 'evict', fetcher, staleTimeMs: 1_000, signal: first.signal });
  const b = fetchQuery({ key: 'evict', fetcher, staleTimeMs: 1_000, signal: second.signal });

  first.abort();
  second.abort();

  await assert.rejects(a, isAbortError);
  await assert.rejects(b, isAbortError);
  assert.equal(upstream?.aborted, true, 'the last subscriber out cancels upstream');
  assert.equal(getSnapshot('evict'), undefined);

  const retry = await fetchQuery({
    key: 'evict',
    staleTimeMs: 1_000,
    fetcher: async () => {
      calls += 1;
      return 'fresh-request';
    },
  });
  assert.equal(retry, 'fresh-request');
  assert.equal(calls, 2, 'the evicted promise is replaced instead of reused');
});

test('prefix invalidation leaves unrelated keys intact and fresh', async () => {
  clearQueryCache();
  const dayKey = queryKeys.signalsLatest('DAY');
  const weekKey = queryKeys.signalsLatest('WEEK');
  const bookKey = queryKeys.portfolioSummary;
  setQueryData(dayKey, { horizon: 'DAY' }, queryStaleTime.signals);
  setQueryData(weekKey, { horizon: 'WEEK' }, queryStaleTime.signals);
  setQueryData(bookKey, { totalEquityPhp: 1 }, queryStaleTime.portfolio);

  invalidateQueries('signals:');

  assert.equal(isQueryFresh(dayKey), false);
  assert.equal(isQueryFresh(weekKey), false);
  assert.equal(isQueryFresh(bookKey), true);
  assert.deepEqual(getSnapshot(bookKey), { totalEquityPhp: 1 });

  let calls = 0;
  const revalidated = await fetchQuery({
    key: dayKey,
    staleTimeMs: queryStaleTime.signals,
    fetcher: async () => {
      calls += 1;
      return { horizon: 'DAY', refreshed: true };
    },
  });
  assert.equal(calls, 1);
  assert.deepEqual(revalidated, { horizon: 'DAY', refreshed: true });
});

test('prefetch failures stay silent and subscribers hear about new data', async () => {
  clearQueryCache();
  let notified = 0;
  const unsubscribe = subscribe('prefetch:key', () => {
    notified += 1;
  });

  await prefetchQuery({
    key: 'prefetch:key',
    staleTimeMs: 1_000,
    fetcher: async () => {
      throw new Error('offline');
    },
  });

  assert.equal(getSnapshot('prefetch:key'), undefined);
  assert.equal(notified, 0);

  setQueryData('prefetch:key', 'warm', 1_000);
  assert.equal(notified, 1);

  unsubscribe();
  invalidateQueries('prefetch:');
  assert.equal(notified, 1, 'unsubscribed listeners stop hearing about invalidations');
});

test('a research read with the reader\'s own cost is a different cache entry', () => {
  const plain = queryKeys.researchItem('WEEK', 'jfc');
  const owned = queryKeys.researchItem('WEEK', 'jfc', { averageCost: 100, shares: 100 });
  const costOnly = queryKeys.researchItem('WEEK', 'jfc', { averageCost: 100 });

  assert.notEqual(owned, plain, 'a position check must never serve the plain buy plan');
  assert.notEqual(costOnly, owned, 'shares change the peso result, so they change the key');
  assert.equal(queryKeys.researchItem('WEEK', '  jfc ', { averageCost: 100, shares: 100 }), owned);
  assert.equal(plain, queryKeys.researchItem('WEEK', 'JFC'), 'the plain plan key stays stable');

  // Prefix invalidation still reaches every variant of the ticker.
  clearQueryCache();
  setQueryData(plain, 'plain', 1_000);
  setQueryData(owned, 'owned', 1_000);
  invalidateQueries('research:item:');

  assert.equal(isQueryFresh(plain), false);
  assert.equal(isQueryFresh(owned), false);
});

