import { knowledgeApi, portfolioApi, researchApi, signalsApi } from '../api/signalsApi';
import type { Horizon } from '../types/signals';
import { prefetchQuery, queryKeys, queryStaleTime } from './queryCache';

/**
 * Warm-up helpers for the heavy dialogs and the read endpoints behind them.
 *
 * Every loader is idempotent: the browser/Vite dedupes the `import()` and `queryCache`
 * dedupes the request, so a pointer sweep across the command bar costs one chunk and one
 * HTTP call, not one per hover. Warm-ups never surface an error and **never** touch the
 * live-feed endpoints (`/backtest`, `/research/{query}`, `/run-now`) — those stay strictly
 * behind an explicit user action because the server recomputes them on every call.
 */

const chunkLoaders = {
  portfolio: () => import('../components/PortfolioDialog'),
  scorecard: () => import('../components/ScorecardDialog'),
  research: () => import('../components/ResearchDialog'),
  tickerDetail: () => import('../components/TickerDetailDialog'),
  knowledge: () => import('../components/KnowledgeBaseDialog'),
  guide: () => import('../components/BeginnerGuideDialog'),
  signalTable: () => import('../components/SignalTable'),
} as const;

type ChunkName = keyof typeof chunkLoaders;

const chunkPromises = new Map<ChunkName, Promise<unknown>>();

function warmChunk(name: ChunkName): void {
  if (chunkPromises.has(name)) {
    return;
  }

  const promise = chunkLoaders[name]().catch(() => undefined);
  chunkPromises.set(name, promise);
}

/** Portfolio sheet: chunk plus the summary the Book tab paints first. */
export function preloadPortfolioDialog(): void {
  warmChunk('portfolio');
  void prefetchQuery({
    key: queryKeys.portfolioSummary,
    staleTimeMs: queryStaleTime.portfolio,
    fetcher: (signal) => portfolioApi.summary(signal),
  });
}

/** Performance sheet: its first tab is the scorecard, so warm that read too. */
export function preloadScorecardDialog(horizon: Horizon): void {
  warmChunk('scorecard');
  void prefetchQuery({
    key: queryKeys.scorecard(horizon),
    staleTimeMs: queryStaleTime.scorecard,
    fetcher: (signal) => signalsApi.scorecard(horizon, signal),
  });
}

/** Desk-robot reports shown inside the Performance sheet's Trainer tab. */
export function preloadModelReports(horizon: Horizon): void {
  void prefetchQuery({
    key: queryKeys.models,
    staleTimeMs: queryStaleTime.models,
    fetcher: (signal) => signalsApi.models(signal),
  });
  void prefetchQuery({
    key: queryKeys.deskMemory(horizon),
    staleTimeMs: queryStaleTime.deskMemory,
    fetcher: (signal) => signalsApi.deskMemory(horizon, signal),
  });
}

/** Research sheet: chunk plus the ticker catalog the search box autocompletes from. */
export function preloadResearchDialog(): void {
  warmChunk('research');
  void prefetchQuery({
    key: queryKeys.researchCatalog,
    staleTimeMs: queryStaleTime.researchCatalog,
    fetcher: (signal) => researchApi.catalog(signal),
  });
}

/** Ticker detail sheet: chunk plus the history the sparkline and trade plan need. */
export function preloadTickerDetailDialog(ticker: string, horizon: Horizon): void {
  warmChunk('tickerDetail');
  void prefetchQuery({
    key: queryKeys.tickerHistory(ticker, horizon),
    staleTimeMs: queryStaleTime.signals,
    fetcher: (signal) => signalsApi.tickerHistory(ticker, horizon, signal),
  });
}

/** Knowledge sheet: chunk plus the document list. */
export function preloadKnowledgeBaseDialog(): void {
  warmChunk('knowledge');
  void prefetchQuery({
    key: queryKeys.knowledgeList,
    staleTimeMs: queryStaleTime.knowledge,
    fetcher: (signal) => knowledgeApi.list(signal),
  });
}

/** Guide sheet is static copy, so only its chunk is worth warming. */
export function preloadBeginnerGuideDialog(): void {
  warmChunk('guide');
}

/** Pro's Top 10 table is its own chunk; warm it when the user reaches for Pro. */
export function preloadSignalTable(): void {
  warmChunk('signalTable');
}

/**
 * Horizon tab warm-up. Pass the visible run date to warm that stored run, or `null` for the
 * live latest run (which is what a horizon switch actually loads).
 */
export function prefetchHorizon(horizon: Horizon, selectedDate: string | null): void {
  if (selectedDate) {
    void prefetchQuery({
      key: queryKeys.signalsByDate(selectedDate, horizon),
      staleTimeMs: queryStaleTime.signals,
      fetcher: (signal) => signalsApi.byDate(selectedDate, horizon, signal),
    });
    return;
  }

  void prefetchQuery({
    key: queryKeys.signalsLatest(horizon),
    staleTimeMs: queryStaleTime.signals,
    fetcher: (signal) => signalsApi.latest(horizon, signal),
  });
}
