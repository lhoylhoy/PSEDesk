import type { Horizon } from '../types/signals';

/**
 * A dependency-free stale-while-revalidate cache with in-flight request de-duplication.
 *
 * It is deliberately React-free so the whole contract can be exercised by `node:test`
 * without a DOM. Two rules keep it honest:
 *
 * 1. It caches **verbatim server payloads**. Nothing here derives, rounds or "fixes" a
 *    figure — the server owns every number, and this module only decides *when* to ask
 *    for them again.
 * 2. Callers attach to **one shared upstream request** per key. The browser may abort a
 *    request for many reasons (StrictMode remounts, horizon switches, dialog closes); the
 *    upstream `fetch` is cancelled only once every attached caller has gone away, so a
 *    hover prefetch followed 60 ms later by a click reuses the same request instead of
 *    producing an avoidable `499 ClientClosedRequest` on the API.
 */

/** Canonical cache keys. Every read endpoint has exactly one spelling. */
export const queryKeys = {
  signalsLatest: (horizon: Horizon) => `signals:latest:${horizon}`,
  signalsByDate: (date: string, horizon: Horizon) => `signals:date:${date}:${horizon}`,
  tickerHistory: (ticker: string, horizon: Horizon) => `signals:history:${ticker}:${horizon}`,
  scorecard: (horizon: Horizon) => `signals:scorecard:${horizon}`,
  models: 'signals:models',
  deskMemory: (horizon: Horizon) => `signals:memory:${horizon}`,
  backtest: (horizon: Horizon, lookbackDays?: number) =>
    `signals:backtest:${horizon}:${lookbackDays ?? 'default'}`,
  researchCatalog: 'research:catalog',
  /**
   * A holding-aware spelling: the same name researched with and without an average cost
   * must never share a cached payload, or the "I own it" answer would leak into the plain
   * buy view (and vice versa).
   */
  researchItem: (horizon: Horizon, query: string, holding?: { averageCost?: number; shares?: number }) => {
    const base = `research:item:${horizon}:${query.trim().toUpperCase()}`;
    if (!holding || (holding.averageCost === undefined && holding.shares === undefined)) {
      return base;
    }

    return `${base}:own:${holding.averageCost ?? 'none'}:${holding.shares ?? 'none'}`;
  },
  portfolioSummary: 'portfolio:summary',
  knowledgeList: 'knowledge:list',
} as const;

/** How long each family of server reads may be served from memory before revalidating. */
export const queryStaleTime = {
  signals: 30_000,
  scorecard: 30_000,
  models: 30_000,
  deskMemory: 30_000,
  /** The replay is heavy and only ever requested by an explicit tap, never prefetched. */
  backtest: 60_000,
  researchCatalog: 300_000,
  researchItem: 45_000,
  portfolio: 15_000,
  knowledge: 60_000,
} as const;

interface Inflight {
  controller: AbortController;
  promise: Promise<unknown>;
  /** Callers currently waiting on this request. The upstream aborts at zero. */
  refCount: number;
}

interface CacheEntry {
  data: unknown;
  hasData: boolean;
  updatedAt: number;
  staleTimeMs: number;
  inflight: Inflight | null;
}

type Listener = () => void;

const entries = new Map<string, CacheEntry>();
const subscribers = new Map<string, Set<Listener>>();

function createAbortError(): Error {
  if (typeof DOMException !== 'undefined') {
    return new DOMException('The operation was aborted.', 'AbortError');
  }

  const error = new Error('The operation was aborted.');
  error.name = 'AbortError';
  return error;
}

function ensureEntry(key: string): CacheEntry {
  let entry = entries.get(key);

  if (!entry) {
    entry = {
      data: undefined,
      hasData: false,
      updatedAt: 0,
      staleTimeMs: 0,
      inflight: null,
    };
    entries.set(key, entry);
  }

  return entry;
}

function notify(key: string): void {
  const listeners = subscribers.get(key);

  if (!listeners) {
    return;
  }

  for (const listener of [...listeners]) {
    try {
      listener();
    } catch {
      // A listener must never break the cache for everyone else.
    }
  }
}

/** The last server payload for a key, or `undefined` when nothing has landed yet. */
export function getSnapshot<T>(key: string): T | undefined {
  const entry = entries.get(key);
  return entry?.hasData ? (entry.data as T) : undefined;
}

/** True when a cached payload is still inside its stale window (a hit would not refetch). */
export function isQueryFresh(key: string): boolean {
  const entry = entries.get(key);

  if (!entry || !entry.hasData) {
    return false;
  }

  return Date.now() - entry.updatedAt < entry.staleTimeMs;
}

/**
 * Seeds a key directly. Used after a mutation, where the server has already returned the
 * authoritative summary and asking for it again would be a wasted round trip.
 */
export function setQueryData<T>(key: string, data: T, staleTimeMs?: number): void {
  const entry = ensureEntry(key);
  entry.data = data;
  entry.hasData = true;
  entry.updatedAt = Date.now();

  if (staleTimeMs !== undefined) {
    entry.staleTimeMs = staleTimeMs;
  }

  notify(key);
}


export interface FetchQueryOptions<T> {
  key: string;
  fetcher: (signal: AbortSignal) => Promise<T>;
  staleTimeMs?: number;
  signal?: AbortSignal;
  /** Revalidate even when the cached payload is still fresh (explicit refresh). */
  force?: boolean;
}

/**
 * Reads a key, refetching when it is stale or missing. Concurrent callers for the same key
 * share one `Promise` and one upstream `AbortController`.
 */
export async function fetchQuery<T>(options: FetchQueryOptions<T>): Promise<T> {
  const entry = ensureEntry(options.key);

  if (options.staleTimeMs !== undefined) {
    entry.staleTimeMs = options.staleTimeMs;
  }

  const fresh =
    !options.force && entry.hasData && Date.now() - entry.updatedAt < entry.staleTimeMs;

  if (fresh) {
    return entry.data as T;
  }

  if (options.signal?.aborted) {
    throw createAbortError();
  }

  const inflight =
    entry.inflight ?? startInflight(entry, options.key, options.fetcher, options.staleTimeMs);

  return attachCaller<T>(entry, inflight, options.signal);
}

/**
 * Fire-and-forget warm-up. Failures are swallowed on purpose: a prefetch is a convenience,
 * never a reason to show an error.
 */
export function prefetchQuery<T>(options: FetchQueryOptions<T>): Promise<void> {
  return fetchQuery<T>(options)
    .then(() => undefined)
    .catch(() => undefined);
}

/** Marks every key with the prefix stale while keeping its data on screen. */
export function invalidateQueries(prefix: string): void {
  for (const [key, entry] of entries) {
    if (!key.startsWith(prefix)) {
      continue;
    }

    entry.updatedAt = 0;
    notify(key);
  }
}

/** Notifies when a key's data or freshness changes. Returns its unsubscribe function. */
export function subscribe(key: string, listener: Listener): () => void {
  const listeners = subscribers.get(key) ?? new Set<Listener>();
  listeners.add(listener);
  subscribers.set(key, listeners);

  return () => {
    listeners.delete(listener);

    if (listeners.size === 0) {
      subscribers.delete(key);
    }
  };
}

/** Wipes the store. Tests and a hard sign-out use this; the terminal never does. */
export function clearQueryCache(): void {
  entries.clear();
  subscribers.clear();
}

function startInflight<T>(
  entry: CacheEntry,
  key: string,
  fetcher: (signal: AbortSignal) => Promise<T>,
  staleTimeMs: number | undefined,
): Inflight {
  const controller = new AbortController();
  const inflight: Inflight = {
    controller,
    refCount: 0,
    promise: Promise.resolve(),
  };

  if (staleTimeMs !== undefined) {
    entry.staleTimeMs = staleTimeMs;
  }

  inflight.promise = (async () => {
    try {
      const value = await fetcher(controller.signal);

      if (entry.inflight === inflight) {
        entry.inflight = null;
        entry.data = value;
        entry.hasData = true;
        entry.updatedAt = Date.now();
      }

      notify(key);
      return value;
    } catch (error) {
      if (entry.inflight === inflight) {
        entry.inflight = null;
      }

      throw error;
    }
  })();

  entry.inflight = inflight;
  return inflight;
}

function attachCaller<T>(
  entry: CacheEntry,
  inflight: Inflight,
  signal: AbortSignal | undefined,
): Promise<T> {
  inflight.refCount += 1;

  return new Promise<T>((resolve, reject) => {
    let settled = false;

    const detach = () => {
      settled = true;
      signal?.removeEventListener('abort', onAbort);
    };

    const onAbort = () => {
      if (settled) {
        return;
      }

      detach();
      inflight.refCount -= 1;

      // Only the last caller leaving may cancel the upstream request.
      if (inflight.refCount <= 0) {
        if (entry.inflight === inflight) {
          entry.inflight = null;
        }

        inflight.controller.abort();
      }

      reject(createAbortError());
    };

    if (signal) {
      signal.addEventListener('abort', onAbort, { once: true });
    }

    inflight.promise.then(
      (value) => {
        if (settled) {
          return;
        }

        detach();
        resolve(value as T);
      },
      (error: unknown) => {
        if (settled) {
          return;
        }

        detach();
        reject(error);
      },
    );
  });
}
