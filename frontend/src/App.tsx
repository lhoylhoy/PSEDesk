import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from 'react';
import { portfolioApi, researchApi, signalsApi } from './api/signalsApi';
import { AdviceCardList } from './components/AdviceCardList';
import { DropPanel } from './components/DropPanel';
import { HowToReadStrip } from './components/HowToReadStrip';
import { LegalDisclaimerGate } from './components/LegalDisclaimerGate';
import { QuotationReportImportButton } from './components/QuotationReportImportButton';
import { RegimeStrip } from './components/RegimeStrip';
import { TerminalHeader } from './components/TerminalHeader';
import { WelcomeSetupBanner } from './components/WelcomeSetupBanner';
import { Button } from './components/ui/Button';
import { TechnicalDisclosure } from './components/ui/TechnicalDisclosure';
import { TooltipProvider } from './components/ui/Tooltip';
import { formatCompactPeso, formatElapsedSeconds, formatTimestamp, formatWeekday } from './lib/formatters';
import { horizonMeta } from './lib/horizons';
import { dismissHowToRead, isHowToReadDismissed, resetHowToRead } from './lib/howToRead';
import {
  clearLegalAcceptance,
  hasAcceptedCurrentLegalVersion,
  recordLegalAcceptance,
} from './lib/legalConsent';
import {
  describeFriendlyError,
  describeHistoryBanner,
  describePortfolioAlert,
  type FriendlyError,
} from './lib/plainAdvice';
import { isViewingHistory, newestSessionDate } from './lib/sessionSelection';
import { preloadKeyboardShortcutsDialog, preloadSignalTable } from './lib/preloaders';
import { isTypingTarget, resolveShortcut, type ShortcutActionId } from './lib/shortcuts';
import {
  fetchQuery,
  getSnapshot,
  invalidateQueries,
  isQueryFresh,
  prefetchQuery,
  queryKeys,
  queryStaleTime,
} from './lib/queryCache';
import { useTheme } from './lib/theme';
import { useViewMode } from './lib/viewMode';
import type {
  Horizon,
  LatestSignalsResponse,
  QuotationReportImportResult,
  SignalItem,
} from './types/signals';
import type { PortfolioSummary, TradeDraft } from './types/portfolio';

type LoadState = 'loading' | 'ready' | 'error';
type BusyAction = 'run' | null;

// Dialogs and the Pro table are route-sized chunks: nothing downloads them until the reader
// asks for one. `preloaders.ts` warms the same modules on hover/focus, so the tap itself is
// usually a cache hit rather than a spinner.
const PortfolioDialog = lazy(async () => ({
  default: (await import('./components/PortfolioDialog')).PortfolioDialog,
}));
const ScorecardDialog = lazy(async () => ({
  default: (await import('./components/ScorecardDialog')).ScorecardDialog,
}));
const ResearchDialog = lazy(async () => ({
  default: (await import('./components/ResearchDialog')).ResearchDialog,
}));
const TickerDetailDialog = lazy(async () => ({
  default: (await import('./components/TickerDetailDialog')).TickerDetailDialog,
}));
const KnowledgeBaseDialog = lazy(async () => ({
  default: (await import('./components/KnowledgeBaseDialog')).KnowledgeBaseDialog,
}));
const BeginnerGuideDialog = lazy(async () => ({
  default: (await import('./components/BeginnerGuideDialog')).BeginnerGuideDialog,
}));
const KeyboardShortcutsDialog = lazy(async () => ({
  default: (await import('./components/KeyboardShortcutsDialog')).KeyboardShortcutsDialog,
}));
const SignalTable = lazy(async () => ({
  default: (await import('./components/SignalTable')).SignalTable,
}));

export default function App() {
  const [horizon, setHorizon] = useState<Horizon>('WEEK');
  // A first run must read the legal notice before it can reach a model verdict. The flag lives in
  // localStorage (see lib/legalConsent.ts); everything else about the reader stays unrecorded.
  const [legalAccepted, setLegalAccepted] = useState(() => hasAcceptedCurrentLegalVersion());
  // Persisted presentation layer: plain-English advice by default, dense terminal on demand.
  const [viewMode, setViewMode] = useViewMode();
  const [theme, setTheme] = useTheme();
  const [selectedDate, setSelectedDate] = useState<string | null>(null);
  const [data, setData] = useState<LatestSignalsResponse | null>(null);
  const [loadState, setLoadState] = useState<LoadState>('loading');
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<FriendlyError | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState<BusyAction>(null);
  const [runElapsedSeconds, setRunElapsedSeconds] = useState(0);
  const [reloadToken, setReloadToken] = useState(0);
  const [activeTicker, setActiveTicker] = useState<string | null>(null);
  const [knowledgeOpen, setKnowledgeOpen] = useState(false);
  const [guideOpen, setGuideOpen] = useState(false);
  const [scorecardOpen, setScorecardOpen] = useState(false);
  const [researchOpen, setResearchOpen] = useState(false);
  const [portfolioOpen, setPortfolioOpen] = useState(false);
  const [tradeDraft, setTradeDraft] = useState<TradeDraft | null>(null);
  const [portfolio, setPortfolio] = useState<PortfolioSummary | null>(null);
  const [portfolioToken, setPortfolioToken] = useState(0);
  const [shortcutsOpen, setShortcutsOpen] = useState(false);
  // The "three steps" strip is remembered once dismissed; the footer link can always bring it back.
  const [howToReadHidden, setHowToReadHidden] = useState(() => isHowToReadDismissed());

  const refreshPortfolio = useCallback(() => {
    invalidateQueries(queryKeys.portfolioSummary);
    setPortfolioToken((token) => token + 1);
  }, []);
  const consumeDraft = useCallback(() => setTradeDraft(null), []);

  /**
   * A screen run downloads a few hundred candles, screens the universe and calls the model, so
   * the reader gets a clock instead of a frozen button. The strip reports elapsed wall clock
   * only — it never claims a stage the server did not confirm.
   */
  useEffect(() => {
    if (busy !== 'run') {
      setRunElapsedSeconds(0);
      return;
    }

    const startedAt = Date.now();
    const timer = window.setInterval(() => {
      setRunElapsedSeconds(Math.floor((Date.now() - startedAt) / 1_000));
    }, 1_000);

    return () => window.clearInterval(timer);
  }, [busy]);

  // The gate shows again when the notice changes, so a reader can never be silently held to an
  // older disclosure.
  const acceptLegalNotice = useCallback(() => {
    recordLegalAcceptance();
    setLegalAccepted(true);
  }, []);

  const reviewLegalNotice = useCallback(() => {
    clearLegalAcceptance();
    setLegalAccepted(false);
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    const cached = getSnapshot<PortfolioSummary>(queryKeys.portfolioSummary);

    if (cached) {
      setPortfolio(cached);
    }

    // Same StrictMode guard as the signal load below: the book is polled on mount, so a
    // synchronous dispatch would be aborted by the development remount while EF Core is
    // still opening SQLite (a debugger-visible TaskCanceledException, answered 499 by the
    // API). Deferring by one macrotask lets the cleanup clear the timer instead.
    const timer = window.setTimeout(() => {
      fetchQuery({
        key: queryKeys.portfolioSummary,
        staleTimeMs: queryStaleTime.portfolio,
        signal: controller.signal,
        fetcher: (signal) => portfolioApi.summary(signal),
      })
        .then((result) => {
          if (!controller.signal.aborted) {
            setPortfolio(result);
          }
        })
        .catch(() => {
          // The blotter is additive: a missing book must never blank the terminal.
        });
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [portfolioToken, reloadToken]);

  useEffect(() => {
    const controller = new AbortController();
    const key = selectedDate
      ? queryKeys.signalsByDate(selectedDate, horizon)
      : queryKeys.signalsLatest(horizon);
    const cached = getSnapshot<LatestSignalsResponse>(key);

    // Paint the switched horizon from memory before asking the server anything. The
    // revalidation below still runs, but the reader never sees a skeleton for a run that
    // was viewed moments ago.
    if (cached) {
      setData(cached);
      setLoadState('ready');
    } else {
      setLoadState('loading');
    }

    setError(null);
    setRefreshing(!isQueryFresh(key));

    // React StrictMode mounts, unmounts and remounts once in development, so a fetch
    // dispatched straight from the effect is always cancelled by the cleanup below.
    // Deferring the dispatch by one macrotask lets that cleanup clear the timer
    // instead, so the API never sees a client-aborted request on every page load
    // (EF Core reports those as a TaskCanceledException while opening SQLite).
    const timer = window.setTimeout(() => {
      fetchQuery({
        key,
        staleTimeMs: queryStaleTime.signals,
        signal: controller.signal,
        fetcher: (signal) =>
          selectedDate
            ? signalsApi.byDate(selectedDate, horizon, signal)
            : signalsApi.latest(horizon, signal),
      })
        .then((response) => {
          if (controller.signal.aborted) {
            return;
          }

          setData(response);
          setLoadState('ready');
          setRefreshing(false);
        })
        .catch((caught: unknown) => {
          if (controller.signal.aborted) {
            return;
          }

          setError(describeFriendlyError(caught));
          setLoadState('error');
          setRefreshing(false);
        });
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [selectedDate, horizon, reloadToken]);

  // The Pro table is a separate chunk; pull it in as soon as the reader reaches for Pro.
  useEffect(() => {
    if (viewMode === 'pro') {
      preloadSignalTable();
    }
  }, [viewMode]);

  // Quietly warm the research catalog while the browser is idle, so opening the sheet and
  // typing a ticker never waits on the full PSE list.
  useEffect(() => {
    const warm = () => {
      void prefetchQuery({
        key: queryKeys.researchCatalog,
        staleTimeMs: queryStaleTime.researchCatalog,
        fetcher: (signal) => researchApi.catalog(signal),
      });
    };

    const idleWindow = window as Window & {
      requestIdleCallback?: (callback: () => void, options?: { timeout: number }) => number;
      cancelIdleCallback?: (handle: number) => void;
    };

    if (typeof idleWindow.requestIdleCallback === 'function') {
      const handle = idleWindow.requestIdleCallback(warm, { timeout: 2_500 });
      return () => idleWindow.cancelIdleCallback?.(handle);
    }

    const timer = window.setTimeout(warm, 1_500);
    return () => window.clearTimeout(timer);
  }, []);

  const refresh = useCallback(() => {
    invalidateQueries('signals:');
    setReloadToken((token) => token + 1);
  }, []);

  const handleRunNow = useCallback(async () => {
    setBusy('run');
    setNotice(null);
    setError(null);

    try {
      const run = await signalsApi.runNow(horizon, selectedDate);
      setNotice(
        viewMode === 'simple'
          ? `Fresh screen for ${formatWeekday(run.runDate)}: ${run.tradeCount + run.smallSizeCount} name${run.tradeCount + run.smallSizeCount === 1 ? '' : 's'
          } cleared the fee-adjusted test, ${run.watchOnlyCount} stayed watch-only, and the model kept about ${run.recommendedCashPct.toFixed(
            0,
          )}% in cash.`
          : `${run.items.length} names ranked for ${formatWeekday(run.runDate)} · ${run.tradeCount} trade / ${run.smallSizeCount
          } small / ${run.watchOnlyCount} watch · ${run.recommendedCashPct.toFixed(0)}% cash · ${run.drops.length
          } exit${run.drops.length === 1 ? '' : 's'} reconciled against the prior run.`,
      );

      if (selectedDate === run.runDate) {
        refresh();
      } else {
        setSelectedDate(run.runDate);
      }

      // A fresh run re-ranks the book, so every cached signal read (and the server-computed
      // order tickets in the portfolio) is now stale.
      invalidateQueries('signals:');
      invalidateQueries('research:item:');
      refreshPortfolio();
    } catch (caught) {
      setError(describeFriendlyError(caught));
    } finally {
      setBusy(null);
    }
  }, [horizon, selectedDate, refresh, refreshPortfolio, viewMode]);

  const run = data?.run ?? null;
  const items = run?.items ?? [];
  const drops = run?.drops ?? [];
  const displayDate = run?.runDate ?? data?.regime?.tradeDate ?? null;
  const portfolioAlerts = portfolio?.alerts.length ?? 0;

  // "Latest" is the newest stored session, not whatever day happens to be on screen. Once the
  // reader steps back, every number below is history and the UI has to say so: re-running it
  // regenerates a past session, which is a different action from fetching today's advice.
  const newestStoredSession = newestSessionDate(data?.dates ?? []);
  const viewingHistory = isViewingHistory(selectedDate, newestStoredSession);

  const returnToLatest = useCallback(() => {
    setNotice(null);
    setSelectedDate(null);
  }, []);

  const openTradeDraft = useCallback((draft: TradeDraft) => {
    setTradeDraft(draft);
    setPortfolioOpen(true);
  }, []);

  /** Step 1 of the strip: land on the market call, not merely scroll near it. */
  const focusMarketCall = useCallback(() => {
    const call = document.getElementById('market-call');
    call?.scrollIntoView({ behavior: 'smooth', block: 'start' });
    call?.focus({ preventScroll: true });
  }, []);

  /**
   * The footer's permanent "How to read this screen" link. It restores the strip the reader
   * dismissed, and falls back to the guide when the strip cannot exist (Pro mode, empty session).
   */
  const showHowToRead = useCallback(() => {
    if (viewMode !== 'simple' || !run) {
      setGuideOpen(true);
      return;
    }

    resetHowToRead();
    setHowToReadHidden(false);
    window.requestAnimationFrame(() => {
      document.getElementById('how-to-read')?.scrollIntoView({ behavior: 'smooth', block: 'center' });
    });
  }, [run, viewMode]);

  const handleTakeSignal = useCallback(
    (item: SignalItem) => {
      const shares = item.orderTicket?.shares ?? item.boardLotShares ?? 0;
      if (shares <= 0) {
        setError({
          title: 'No board lot fits your practice account',
          message: `${item.ticker} costs more per board lot than your practice account can fund. Try a cheaper stock, or raise your play money under My Trades → My Rules.`,
          technical: null,
          retryable: false,
        });
        return;
      }

      setActiveTicker(null);
      openTradeDraft({
        ticker: item.ticker,
        companyName: item.companyName,
        sector: item.sector,
        horizon,
        edgeVerdict: item.edgeVerdict,
        entryPrice: item.entryPrice,
        stopLoss: item.stopLoss,
        targetPrice: item.targetPrice,
        shares,
      });
    },
    [horizon, openTradeDraft],
  );

  const activeItem = useMemo<SignalItem | null>(
    () => items.find((item) => item.ticker === activeTicker) ?? null,
    [items, activeTicker],
  );

  const handleQuotationImported = useCallback(
    (result: QuotationReportImportResult) => {
      setError(null);
      setNotice(
        `Imported the PSE Daily Quotation Report for ${formatWeekday(result.tradeDate)} · ${result.matchedTickerCount
        } tickers matched · ${result.updatedMetricCount} metrics updated · market net foreign ${formatCompactPeso(
          result.marketNetForeignFlowPhp,
        )}${result.suspendedTickers.length > 0
          ? ` · ${result.suspendedTickers.length} suspended (${result.suspendedTickers.join(', ')})`
          : ''
        }.`,
      );

      invalidateQueries('signals:');
      setReloadToken((token) => token + 1);
    },
    [],
  );

  const handleQuotationError = useCallback((caught: unknown) => {
    setNotice(null);
    setError(describeFriendlyError(caught));
  }, []);

  /**
   * Called with the authoritative summary the book already fetched after a mutation. It
   * seeds the shared cache and marks the server-computed signal reads stale — one portfolio
   * request per mutation, not three.
   */
  const handlePortfolioMutated = useCallback((next: PortfolioSummary) => {
    setPortfolio(next);
    invalidateQueries('signals:');
    invalidateQueries('research:item:');
    setReloadToken((token) => token + 1);
  }, []);

  /**
   * True whenever a sheet owns the screen. Shortcuts read it so a keystroke can never reach the
   * page behind a dialog — `?` is the single exception, because it belongs to the sheet itself.
   */
  const anyDialogOpen =
    activeTicker !== null ||
    knowledgeOpen ||
    guideOpen ||
    scorecardOpen ||
    researchOpen ||
    portfolioOpen ||
    shortcutsOpen ||
    !legalAccepted;

  /**
   * `resolveShortcut` decides *whether* a keystroke means something; this decides *what*. Every
   * branch calls the same setter its visible button calls, so a shortcut can never do something
   * the interface could not do with a tap.
   */
  const runShortcut = useCallback(
    (action: ShortcutActionId) => {
      switch (action) {
        case 'horizon-day':
          setNotice(null);
          setSelectedDate(null);
          setHorizon('DAY');
          return;
        case 'horizon-week':
          setNotice(null);
          setSelectedDate(null);
          setHorizon('WEEK');
          return;
        case 'horizon-month':
          setNotice(null);
          setSelectedDate(null);
          setHorizon('MONTH');
          return;
        case 'horizon-year':
          setNotice(null);
          setSelectedDate(null);
          setHorizon('YEAR');
          return;
        case 'run':
          if (busy === null) {
            void handleRunNow();
          }
          return;
        case 'research':
          setResearchOpen(true);
          return;
        case 'trades':
          setTradeDraft(null);
          setPortfolioOpen(true);
          return;
        case 'scorecard':
          setScorecardOpen(true);
          return;
        case 'view-simple':
          setViewMode('simple');
          return;
        case 'view-pro':
          setViewMode('pro');
          return;
        case 'toggle-theme':
          setTheme(theme === 'dark' ? 'light' : 'dark');
          return;
        case 'guide':
          setGuideOpen(true);
          return;
        case 'help':
          // `?` closes its own sheet and refuses to stack on top of a different one.
          if (anyDialogOpen && !shortcutsOpen) {
            return;
          }
          setShortcutsOpen((open) => !open);
          return;
      }
    },
    [anyDialogOpen, busy, handleRunNow, setTheme, setViewMode, shortcutsOpen, theme],
  );

  useEffect(() => {
    if (!legalAccepted) {
      return;
    }

    const onKeyDown = (event: KeyboardEvent) => {
      const action = resolveShortcut(
        { key: event.key, ctrlKey: event.ctrlKey, metaKey: event.metaKey, altKey: event.altKey },
        { typing: isTypingTarget(event.target), dialogOpen: anyDialogOpen },
      );

      if (!action) {
        return;
      }

      event.preventDefault();
      runShortcut(action);
    };

    window.addEventListener('keydown', onKeyDown);
    return () => window.removeEventListener('keydown', onKeyDown);
  }, [anyDialogOpen, legalAccepted, runShortcut]);

  /**
   * One polite live region for the whole shell. Without it a screen-reader user never hears
   * that a run started, that it finished, or why a refresh failed — the page just changes.
   * Elapsed seconds stay out of it on purpose: a live region that ticks every second is noise.
   */
  const liveStatus = error
    ? `${error.title}. ${error.message}`
    : notice ??
      (busy === 'run'
        ? `Running the ${horizon} screen. The previous session stays on screen until the new one lands.`
        : '');

  return (
    <TooltipProvider delayDuration={150}>
      {/* First thing in the tab order: the header is a control bar, the signals are the point. */}
      <a
        href="#main-content"
        className="sr-only focus:not-sr-only focus:fixed focus:left-3 focus:top-3 focus:z-50 focus:rounded-lg focus:border focus:border-line focus:bg-ink-850 focus:px-3 focus:py-2 focus:text-sm focus:font-semibold focus:text-fg"
      >
        Skip to the signals
      </a>
      <div className="shell mx-auto flex min-h-dvh w-full max-w-[112rem] flex-col gap-4 3xl:max-w-[128rem] lg:gap-5">
        {/* Mounted from the first render so the region is live before anything changes. */}
        <div aria-live="polite" aria-atomic="true" className="sr-only">
          {liveStatus}
        </div>

        <TerminalHeader
          dates={data?.dates ?? []}
          selectedDate={displayDate}
          historicalSession={viewingHistory}
          onDateChange={(date) => {
            setNotice(null);
            setSelectedDate(date);
          }}
          horizon={horizon}
          onHorizonChange={(next) => {
            setNotice(null);
            setSelectedDate(null);
            setHorizon(next);
          }}
          onRunNow={handleRunNow}
          onOpenKnowledge={() => setKnowledgeOpen(true)}
          onOpenScorecard={() => setScorecardOpen(true)}
          onOpenResearch={() => setResearchOpen(true)}
          onOpenPortfolio={() => {
            setTradeDraft(null);
            setPortfolioOpen(true);
          }}
          onOpenGuide={() => setGuideOpen(true)}
          onOpenShortcuts={() => setShortcutsOpen(true)}
          portfolioAlerts={portfolioAlerts}
          busy={busy}
          viewMode={viewMode}
          onViewModeChange={setViewMode}
          theme={theme}
          onThemeChange={setTheme}
          quotationImport={
            viewMode === 'pro' ? (
              <QuotationReportImportButton
                date={displayDate}
                disabled={busy !== null}
                onImported={handleQuotationImported}
                onError={handleQuotationError}
              />
            ) : undefined
          }
        />

        {/* Everything below the control bar. The skip link lands here. */}
        <main id="main-content" tabIndex={-1} className="flex flex-col gap-4 outline-none lg:gap-5">
          {busy === 'run' ? (
            <div className="rounded-xl border border-sky-400/30 bg-sky-400/5 px-4 py-3">
              <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
                <p className="type-body font-semibold text-fg simple-label">
                  {viewMode === 'simple' ? 'Running the screen…' : `Running the ${horizon} screen…`}
                </p>
                <p className="tabular type-caption text-fg-muted simple-note">
                  {formatElapsedSeconds(runElapsedSeconds)} elapsed
                </p>
              </div>
              <p className="mt-1 type-caption text-fg-muted simple-note">
                Downloading today’s prices and filings, then ranking the liquid names. This usually takes
                under two minutes — the session on screen stays there until the new one lands.
              </p>
              <div aria-hidden className="mt-2 h-1 overflow-hidden rounded-full bg-ink-700">
                <div className="run-progress h-full rounded-full bg-sky-400" />
              </div>
            </div>
          ) : null}

          {viewingHistory ? (
            <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-amber-400/30 bg-amber-400/5 px-4 py-3 text-amber-400">
              <span className="type-caption simple-copy">{describeHistoryBanner(selectedDate, viewMode === 'simple')}</span>
              <Button variant="secondary" onClick={returnToLatest} className="h-8 px-3">
                {viewMode === 'simple' ? 'Back to latest' : 'Latest session'}
              </Button>
            </div>
          ) : null}

          <WelcomeSetupBanner
            account={portfolio?.account ?? null}
            onOpenGuide={() => setGuideOpen(true)}
            onOpenTrades={() => {
              setTradeDraft(null);
              setPortfolioOpen(true);
            }}
            onSaved={() => {
              refreshPortfolio();
              refresh();
            }}
          />

          {notice ? (
            <div className="rounded-xl border border-mint-400/30 bg-mint-400/5 px-4 py-3 text-mint-400">
              <p className="type-caption simple-copy">{notice}</p>
            </div>
          ) : null}

          {portfolio && portfolio.alerts.length > 0 ? (
            <div className="flex flex-wrap items-center justify-between gap-3 rounded-xl border border-amber-400/40 bg-amber-400/5 px-4 py-3 text-amber-400">
              <span className="type-caption simple-copy">
                {viewMode === 'simple' ? describePortfolioAlert(portfolio.alerts[0]) : portfolio.alerts[0]}
                {portfolio.alerts.length > 1 ? ` (+${portfolio.alerts.length - 1} more)` : ''}
              </span>
              <Button
                variant="secondary"
                onClick={() => {
                  setTradeDraft(null);
                  setPortfolioOpen(true);
                }}
              >
                {viewMode === 'simple' ? 'Open My Trades' : 'Open blotter'}
              </Button>
            </div>
          ) : null}

          {error ? (
            <div className="flex flex-wrap items-start justify-between gap-3 rounded-xl border border-rose-400/40 bg-rose-400/5 px-4 py-3 text-rose-400">
              <div className="measure-wide min-w-0">
                <p className="type-body font-semibold simple-copy">{error.title}</p>
                <p className="mt-1 type-caption text-fg-muted simple-copy">{error.message}</p>
                {error.technical ? (
                  <details className="mt-2">
                    <summary className="cursor-pointer text-2xs text-fg-subtle simple-note">
                      Technical details
                    </summary>
                    <p className="mt-1 type-caption wrap-break-word font-mono text-fg-subtle simple-note">
                      {error.technical}
                    </p>
                  </details>
                ) : null}
              </div>
              <Button variant="secondary" onClick={refresh}>
                Try again
              </Button>
            </div>
          ) : null}

          {loadState === 'loading' && !data ? (
            <div
              className="panel h-[clamp(8rem,18vh,12rem)] animate-pulse"
              aria-label="Loading signal desk"
            />
          ) : null}

          {data ? (
            <>
              <RegimeStrip
                regime={data.regime}
                horizon={horizon}
                run={run}
                viewMode={viewMode}
                trustPulse={data.trustPulse}
                onOpenScorecard={() => setScorecardOpen(true)}
              />

              {run ? (
                viewMode === 'simple' ? (
                  <>
                    {howToReadHidden ? null : (
                      <HowToReadStrip
                        onReadCall={focusMarketCall}
                        onOpenSignal={() => {
                          if (items.length > 0) {
                            setActiveTicker(items[0].ticker);
                          } else {
                            setResearchOpen(true);
                          }
                        }}
                        onPractise={() => {
                          setTradeDraft(null);
                          setPortfolioOpen(true);
                        }}
                        onDismiss={() => {
                          dismissHowToRead();
                          setHowToReadHidden(true);
                        }}
                      />
                    )}
                    <AdviceCardList
                      items={items}
                      horizon={horizon}
                      runDate={run.runDate}
                      historical={viewingHistory}
                      onSelect={setActiveTicker}
                    />
                    <DropPanel drops={drops} friendly />
                  </>
                ) : (
                  <>
                    <div className="flex flex-wrap items-end justify-between gap-2 px-1">
                      <div>
                        <h2 className="type-title text-fg">
                          {formatWeekday(run.runDate)} · {horizonMeta[horizon].label} book · {items.length} ranked
                          names
                        </h2>
                        <p className="mt-1 type-caption measure-wide text-fg-subtle">
                          Generated {formatTimestamp(run.createdAtUtc)} · drift recomputed server-side ·{' '}
                          {run.tradeCount} trade / {run.smallSizeCount} small / {run.watchOnlyCount} watch ·{' '}
                          {run.recommendedCashPct.toFixed(0)}% cash ·{' '}
                          {drops.length} exit{drops.length === 1 ? '' : 's'} versus the prior session
                        </p>
                      </div>
                      <div className="flex items-center gap-2">
                        {refreshing && data ? (
                          <span role="status" className="type-eyebrow text-fg-faint">
                            Refreshing…
                          </span>
                        ) : null}
                        <QuotationReportImportButton
                          date={displayDate}
                          disabled={busy !== null}
                          onImported={handleQuotationImported}
                          onError={handleQuotationError}
                        />
                      </div>
                    </div>

                    <div className="grid gap-4 3xl:grid-cols-[minmax(0,1fr)_22rem] 3xl:items-start 3xl:gap-5 4xl:grid-cols-[minmax(0,1fr)_26rem]">
                      <Suspense
                        fallback={
                          <div
                            className="panel h-96 animate-pulse"
                            aria-label="Loading the ranked table"
                          />
                        }
                      >
                        <SignalTable
                          items={items}
                          horizon={horizon}
                          onSelect={setActiveTicker}
                          onTakeTrade={handleTakeSignal}
                          recommendedCashPct={run.recommendedCashPct}
                        />
                      </Suspense>
                      <DropPanel drops={drops} />
                    </div>
                  </>
                )
              ) : (
                <section className="panel flex flex-col items-start gap-4 p-5 sm:p-6">
                  <h2 className="type-title text-fg">
                    {viewMode === 'simple'
                      ? data.dates.length === 0
                        ? 'No prices yet — run the first screen'
                        : 'Prices are loaded, but no advice has been computed yet'
                      : data.dates.length === 0
                        ? 'No live session stored yet'
                        : 'Market data loaded, no signal run yet'}
                  </h2>
                  {viewMode === 'simple' ? (
                    <p className="measure type-body text-fg-muted">
                      Press <span className="font-semibold text-fg">Run Latest Screen</span> and the desk
                      downloads today’s PSE prices and company filings, then reports which stocks clear its
                      fee-adjusted test, the Peso levels the model used, and where its cut-loss sits. The first run
                      needs an internet connection.
                    </p>
                  ) : (
                    <p className="measure type-body text-fg-muted">
                      {data.dates.length === 0
                        ? 'This desk is live-only: candles come from Yahoo Finance (PSEI.PS, PHP=X, {TICKER}.PS), disclosures from PSE Edge, playbooks from the SQLite knowledge base and ranking from Ollama Cloud. Nothing is seeded or simulated, so the first run needs network access plus OLLAMA_API_KEY. If any feed is down the API answers 503 instead of inventing data.'
                        : 'This session has a live market snapshot but no ranked list for this horizon. Run the screen to score the liquid universe, retrieve the matching playbooks, call the desk model and store the ranked list.'}
                    </p>
                  )}
                  <div className="flex flex-wrap gap-2">
                    <Button variant="primary" busy={busy === 'run'} onClick={handleRunNow} disabled={busy !== null}>
                      {viewMode === 'simple' ? 'Run Latest Screen' : 'Run screen'}
                    </Button>
                    {viewMode === 'simple' ? null : (
                      <Button variant="secondary" onClick={() => setKnowledgeOpen(true)}>
                        Knowledge base
                      </Button>
                    )}
                    {viewMode === 'simple' ? null : (
                      <QuotationReportImportButton
                        date={displayDate}
                        disabled={busy !== null}
                        onImported={handleQuotationImported}
                        onError={handleQuotationError}
                      />
                    )}
                  </div>
                  {viewMode === 'simple' ? (
                    <TechnicalDisclosure title="Setup & technical information" className="w-full measure-wide">
                      <p className="type-body text-fg-muted">
                        {data.dates.length === 0
                          ? 'This desk is live-only: candles come from Yahoo Finance (PSEI.PS, PHP=X, {TICKER}.PS), disclosures from PSE Edge, playbooks from the SQLite knowledge base and ranking from Ollama Cloud. Nothing is seeded or simulated, so the first run needs network access plus OLLAMA_API_KEY. If any feed is down the API answers 503 instead of inventing data.'
                          : 'This session has a live market snapshot but no ranked list for this horizon. Run the screen to score the liquid universe, retrieve the matching playbooks, call the desk model and store the ranked list.'}
                      </p>
                    </TechnicalDisclosure>
                  ) : null}
                </section>
              )}
            </>
          ) : null}
        </main>

        <footer className="mt-auto flex flex-wrap items-start justify-between gap-x-4 gap-y-2 border-t border-line/60 px-1 pt-4 type-caption text-fg-faint">
          {/* Every shortcut is also a visible control, and the whole map is a click away here —
              so nothing about this desk requires a keyboard to discover. */}
          <span className="flex flex-wrap items-center gap-x-3 gap-y-1.5">
            <button
              type="button"
              onClick={() => setGuideOpen(true)}
              className="underline decoration-dotted underline-offset-2 transition hover:text-fg-muted"
            >
              {viewMode === 'simple' ? 'How it works' : 'Guide'} <kbd className="kbd ml-0.5">G</kbd>
            </button>
            <button
              type="button"
              onClick={() => setShortcutsOpen(true)}
              onPointerEnter={preloadKeyboardShortcutsDialog}
              onFocus={preloadKeyboardShortcutsDialog}
              className="underline decoration-dotted underline-offset-2 transition hover:text-fg-muted"
            >
              Keyboard shortcuts <kbd className="kbd ml-0.5">?</kbd>
            </button>
            <button
              type="button"
              onClick={showHowToRead}
              className="underline decoration-dotted underline-offset-2 transition hover:text-fg-muted"
            >
              How to read this screen
            </button>
          </span>
          <span className="flex flex-col gap-1 sm:items-end">
            <span>
              {viewMode === 'simple'
                ? 'PSE Signal Desk · four holding periods · prices already include broker fees · Manila time (UTC+8)'
                : 'PSE Signal Desk · four horizons · board-lot rounded plans · Manila time (UTC+8)'}
            </span>
            <span className="flex flex-wrap items-center gap-2">
              <span>
                Research tooling only, not investment advice. Prices from Yahoo Finance and PSE Edge
                under a personal, non-commercial posture.
              </span>
              <button
                type="button"
                onClick={reviewLegalNotice}
                className="underline decoration-dotted underline-offset-2 hover:text-fg-muted"
              >
                Review the notice
              </button>
            </span>
          </span>
        </footer>

        {activeItem ? (
          <Suspense fallback={null}>
            <TickerDetailDialog
              item={activeItem}
              open
              onOpenChange={(open) => {
                if (!open) {
                  setActiveTicker(null);
                }
              }}
              horizon={horizon}
              onTakeTrade={handleTakeSignal}
              viewMode={viewMode}
            />
          </Suspense>
        ) : null}

        {knowledgeOpen ? (
          <Suspense fallback={null}>
            <KnowledgeBaseDialog open onOpenChange={setKnowledgeOpen} />
          </Suspense>
        ) : null}

        {guideOpen ? (
          <Suspense fallback={null}>
            <BeginnerGuideDialog open onOpenChange={setGuideOpen} />
          </Suspense>
        ) : null}

        {scorecardOpen ? (
          <Suspense fallback={null}>
            <ScorecardDialog
              open
              onOpenChange={setScorecardOpen}
              horizon={horizon}
              viewMode={viewMode}
            />
          </Suspense>
        ) : null}

        {researchOpen ? (
          <Suspense fallback={null}>
            <ResearchDialog
              open
              onOpenChange={setResearchOpen}
              horizon={horizon}
              viewMode={viewMode}
              onTakeTrade={(draft) => {
                openTradeDraft(draft);
              }}
            />
          </Suspense>
        ) : null}

        {portfolioOpen ? (
          <Suspense fallback={null}>
            <PortfolioDialog
              open
              onOpenChange={(open) => {
                setPortfolioOpen(open);
                if (!open) {
                  setTradeDraft(null);
                }
              }}
              draft={tradeDraft}
              onDraftConsumed={consumeDraft}
              summary={portfolio}
              onPortfolioMutated={handlePortfolioMutated}
              viewMode={viewMode}
            />
          </Suspense>
        ) : null}
        {shortcutsOpen ? (
          <Suspense fallback={null}>
            <KeyboardShortcutsDialog open onOpenChange={setShortcutsOpen} />
          </Suspense>
        ) : null}
        {legalAccepted ? null : <LegalDisclaimerGate onAccept={acceptLegalNotice} />}
      </div>
    </TooltipProvider>
  );
}
