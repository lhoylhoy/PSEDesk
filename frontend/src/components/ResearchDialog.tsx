import { useEffect, useMemo, useState, type FormEvent } from 'react';
import { researchApi, type ResearchHolding } from '../api/signalsApi';
import type { CatalogStock, EdgeVerdict, Horizon, TickerResearch } from '../types/signals';
import type { TradeDraft } from '../types/portfolio';
import { cn } from '../lib/cn';
import {
  formatCompactPeso,
  formatDate,
  formatInteger,
  formatPercent,
  formatPrice,
  formatRatio,
} from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import {
  describeExecutionGuidance,
  describeFriendlyError,
  describeHoldingVerdict,
  describePlainVerdict,
  describeWinChance,
  translateTicketWarnings,
} from '../lib/plainAdvice';
import {
  POPULAR_PSE_STOCKS,
  companyLabel,
  searchCatalog,
} from '../lib/pseNicknames';
import {
  fetchQuery,
  getSnapshot,
  prefetchQuery,
  queryKeys,
  queryStaleTime,
} from '../lib/queryCache';
import { formatProbability, formatR } from '../lib/riskSizing';
import type { ViewMode } from '../lib/viewMode';
import { PlanSteps, WavePlanGrid } from './AdviceCardList';
import { VerdictBadge, verdictDescription } from './VerdictBadge';
import { Button } from './ui/Button';
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogStickyHeader,
  DialogTitle,
} from './ui/Dialog';
import { OptionalDisclosure } from './ui/TechnicalDisclosure';
import { Tabs, TabsList, TabsTrigger } from './ui/Tabs';
import { Tooltip } from './ui/Tooltip';

interface ResearchDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  horizon: Horizon;
  /** Hands the researched plan to the blotter; the ticket's own share count is kept. */
  onTakeTrade: (draft: TradeDraft) => void;
  /** Simple mode renames the tool and hides the raw tiles inside a drawer. */
  viewMode?: ViewMode;
}

const inputClass =
  'rounded-lg border border-line/80 bg-ink-900/80 px-2.5 py-1.5 text-xs normal-case tracking-normal text-fg outline-none placeholder:text-fg-faint focus:border-mint-400/50';

function Tile({
  label,
  value,
  detail,
  tone,
}: {
  label: string;
  value: string;
  detail?: string;
  tone?: string;
}) {
  return (
    <div className="rounded-xl border border-line/70 bg-ink-850/80 px-3 py-2">
      <p className="type-eyebrow text-fg-subtle">{label}</p>
      <p className={cn('tabular mt-1 text-sm font-semibold text-fg', tone)}>{value}</p>
      {detail ? <p className="tabular mt-0.5 text-2xs text-fg-subtle">{detail}</p> : null}
    </div>
  );
}

function MetricRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-2 border-b border-line/50 py-1.5 last:border-0">
      <span className="text-xs text-fg-subtle">{label}</span>
      <span className="tabular text-xs font-medium text-fg">{value}</span>
    </div>
  );
}

/**
 * The holding scenario: the server's ladder verdict (stop breached → target hit → trail → lock
 * break-even → hold) with the fee-paid result of the reader's own cost basis. Nothing here is
 * inferred in the browser.
 */
function HoldingVerdictCard({ research }: { research: TickerResearch }) {
  const advice = research.positionAdvice;

  if (!advice) {
    return (
      <p className="mt-3 rounded-lg border border-line/70 bg-ink-900/70 px-3 py-2 type-body measure text-fg-muted">
        The desk could not grade this position yet — it needs a priced plan for {research.ticker} first.
      </p>
    );
  }

  const verdict = describeHoldingVerdict(advice);

  return (
    <div className="mt-3 rounded-xl border border-line/70 bg-ink-850/70 px-3 py-2.5">
      <p className="type-eyebrow text-fg-subtle">Your position · {advice.ticker}</p>
      <div className="mt-1 flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <p className={cn('text-sm font-semibold', verdict.tone)}>{verdict.label}</p>
        <span className="text-xs text-fg-muted">{verdict.description}</span>
        {verdict.urgent ? (
          <span className="rounded-full border border-amber-400/40 bg-amber-400/10 px-2 py-0.5 type-badge text-amber-400">
            Act today
          </span>
        ) : null}
      </div>
      <p className="mt-1 type-body measure text-fg-muted">{verdict.detail}</p>

      <dl className="mt-2 grid grid-cols-2 gap-2 xs:grid-cols-3 lg:grid-cols-6">
        <Tile label="Your average" value={formatPrice(advice.averageCost)} />
        <Tile
          label="Price now"
          value={formatPrice(advice.currentPrice)}
          detail={`${formatPercent(advice.netReturnPct, 2)} after fees`}
          tone={advice.netReturnPct >= 0 ? 'text-mint-400' : 'text-rose-400'}
        />
        <Tile label="Break-even" value={formatPrice(advice.breakEvenPrice)} detail="fees paid both ways" />
        <Tile label="Keep stop at" value={formatPrice(advice.suggestedStopPrice)} tone="text-rose-400" />
        <Tile label="Target" value={formatPrice(advice.suggestedTargetPrice)} tone="text-mint-400" />
        <Tile
          label="Trim now"
          value={advice.recommendedTrimShares > 0 ? `${formatInteger(advice.recommendedTrimShares)} sh` : '—'}
          detail={
            advice.shares > 0
              ? `${formatInteger(advice.shares)} sh held · ${formatR(advice.unrealizedR)} open`
              : `${formatR(advice.unrealizedR)} open`
          }
        />
      </dl>

      {advice.unrealizedNetPnlPhp != null ? (
        <p className="mt-1.5 text-2xs text-fg-subtle">
          Fee-paid open result:{' '}
          <span className={cn('tabular font-semibold', advice.unrealizedNetPnlPhp >= 0 ? 'text-mint-400' : 'text-rose-400')}>
            {formatCompactPeso(advice.unrealizedNetPnlPhp)}
          </span>
        </p>
      ) : (
        <p className="mt-1.5 text-2xs text-fg-subtle">
          Add how many shares you hold to see the open result in pesos.
        </p>
      )}
    </div>
  );
}

function ResearchBody({
  research,
  horizon,
  simple,
  focus,
}: {
  research: TickerResearch;
  horizon: Horizon;
  simple: boolean;
  focus: 'buy' | 'own';
}) {
  const metrics = research.metrics;
  const ticket = research.orderTicket;

  return (
    <div className="mt-4">
      <div className="flex flex-wrap items-center gap-2">
        <h3 className="text-lg font-semibold text-fg">{companyLabel(research.ticker, research.companyName)}</h3>
        <span className="font-mono text-xs tracking-wider text-fg-muted">{research.ticker}</span>
        {companyLabel(research.ticker, research.companyName) === research.companyName ? null : (
          <span className="text-xs text-fg-muted">{research.companyName}</span>
        )}
        <span className="rounded border border-line/70 px-1 py-0.5 type-badge text-fg-subtle">
          {research.sector}
        </span>
        <Tooltip label={verdictDescription(research.edgeVerdict)}>
          <span tabIndex={0}>
            <VerdictBadge verdict={research.edgeVerdict} friendly={simple} />
          </span>
        </Tooltip>
        <span className="text-2xs text-fg-faint">
          {simple
            ? `as of ${formatDate(research.asOfDate)}`
            : `${horizonMeta[horizon].label} book · as of ${formatDate(research.asOfDate)} · ${research.dataSource}`}
        </span>
      </div>

      {focus === 'own' ? <HoldingVerdictCard research={research} /> : null}

      {simple ? (
        <>
          <p className="mt-3 type-body measure text-fg-muted">
            {describePlainVerdict(research.edgeVerdict).explanation}{' '}
            {describeWinChance(research.winProbability, research.breakEvenWinRate)}
          </p>
          <div className="mt-3 space-y-3">
            {research.wavePlan ? (
              <WavePlanGrid plan={research.wavePlan} />
            ) : (
              <PlanSteps
                entryPrice={research.plan.entryPrice}
                targetPrice={research.plan.targetPrice}
                stopLoss={research.plan.stopLoss}
              />
            )}
            {ticket ? (
              <div className="rounded-lg border border-mint-400/25 bg-mint-400/5 px-3 py-2">
                <p className="text-xs font-semibold text-fg">
                  Buy {formatInteger(ticket.shares)} share{ticket.shares === 1 ? '' : 's'}
                  <span className="font-normal text-fg-subtle">
                    {' '}
                    ({ticket.lots} board lot{ticket.lots === 1 ? '' : 's'} of {formatInteger(ticket.boardLotSize)})
                  </span>
                </p>
                <p className="mt-1 type-body measure text-fg-muted">
                  About {formatCompactPeso(ticket.buyOutlayPhp)} all-in. If it reaches the target you keep about{' '}
                  <span className="font-semibold text-mint-400">{formatCompactPeso(ticket.netRewardPhp)}</span> after
                  fees; if it hits the cut-loss you lose about{' '}
                  <span className="font-semibold text-rose-400">{formatCompactPeso(ticket.netRiskPhp)}</span>.
                </p>
                <p className="mt-1 type-caption text-fg-subtle">
                  {describeExecutionGuidance(ticket.executionGuidance, research.edgeVerdict !== 'WATCH_ONLY')}
                </p>
                {translateTicketWarnings(ticket.warnings).map((warning) => (
                  <p key={warning} className="mt-1 type-caption measure text-amber-300/90">
                    {warning}
                  </p>
                ))}
              </div>
            ) : (
              <p className="rounded-lg border border-line/70 bg-ink-900/70 px-3 py-2 text-xs text-fg-muted">
                No board lot fits the paper account at this price, so there is nothing to buy today.
              </p>
            )}
          </div>
        </>
      ) : null}

      <OptionalDisclosure enabled={simple} title="Sizing, odds & technical indicators" className="mt-3">
      <dl className="mt-3 grid grid-cols-2 gap-2 xs:grid-cols-3 lg:grid-cols-6">
        <Tile label="Entry" value={formatPrice(research.plan.entryPrice)} />
        <Tile label="Stop" value={formatPrice(research.plan.stopLoss)} tone="text-rose-400" />
        <Tile label="Target" value={formatPrice(research.plan.targetPrice)} tone="text-mint-400" />
        <Tile
          label="Net R:R"
          value={formatRatio(research.plan.netRiskRewardRatio)}
          detail={`gross ${formatRatio(research.plan.riskRewardRatio)}`}
        />
        <Tile
          label="P(win)"
          value={`${formatProbability(research.winProbability)} / ${formatProbability(research.breakEvenWinRate)}`}
          detail={`${formatR(research.expectedValueR)} EV per trade`}
          tone={research.winProbability >= research.breakEvenWinRate ? 'text-mint-400' : 'text-rose-400'}
        />
        <Tile
          label="Quarter-Kelly"
          value={`${(Math.max(0, research.quarterKellyFraction) * 100).toFixed(1)}%`}
          detail={ticket ? `${formatInteger(ticket.shares)} sh funded` : 'no ticket'}
        />
      </dl>

      {ticket ? (
        <div className="mt-3 rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <h4 className="type-eyebrow text-fg-subtle">Server order ticket</h4>
            {ticket.liquidityCapped ? (
              <span className="rounded border border-amber-400/40 bg-amber-400/10 px-1.5 py-0.5 type-badge text-amber-400">
                Liquidity capped
              </span>
            ) : null}
          </div>
          <dl className="mt-2 grid grid-cols-2 gap-2 sm:grid-cols-5">
            <Tile
              label="Shares"
              value={`${formatInteger(ticket.shares)} sh`}
              detail={`${formatInteger(ticket.lots)} lot${ticket.lots === 1 ? '' : 's'} · ${formatInteger(ticket.boardLotSize)}/lot`}
            />
            <Tile label="All-in cost" value={formatPrice(ticket.buyOutlayPhp)} detail="incl. buy leg" />
            <Tile label="Net risk" value={formatPrice(ticket.netRiskPhp)} detail="fee-paid, both legs" />
            <Tile label="Net reward" value={formatPrice(ticket.netRewardPhp)} detail="fee-paid" />
            <Tile
              label="Break-even stop"
              value={formatPrice(ticket.breakEvenStopPrice)}
              detail={`budget ${formatPrice(ticket.riskBudgetPhp)}`}
            />
          </dl>
          {ticket.warnings.length > 0 ? (
            <ul className="mt-2 space-y-1">
              {ticket.warnings.map((warning) => (
                <li key={warning} className="type-caption measure text-amber-400/90">
                  {warning}
                </li>
              ))}
            </ul>
          ) : null}
        </div>
      ) : null}


      <div className="mt-3 grid gap-3 lg:grid-cols-2">
        <section className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <h4 className="type-eyebrow text-fg-subtle">Technicals · {research.ticker}</h4>
          {metrics ? (
            <div className="mt-2">
              <MetricRow
                label="Close"
                value={`${formatPrice(metrics.closePrice)} (${formatPercent(metrics.changePct)})`}
              />
              <MetricRow label="RSI 14" value={metrics.rsi14.toFixed(1)} />
              <MetricRow label="ATR 14" value={formatPrice(metrics.atr14)} />
              <MetricRow label="EMA 20" value={formatPrice(metrics.ema20)} />
              <MetricRow label="SMA 50" value={formatPrice(metrics.sma50)} />
              <MetricRow label="SMA 200" value={formatPrice(metrics.sma200)} />
              <MetricRow label="52-week high" value={formatPrice(metrics.week52High)} />
              <MetricRow label="Support" value={formatPrice(metrics.supportLevel)} />
              <MetricRow label="Resistance" value={formatPrice(metrics.resistanceLevel)} />
              <MetricRow label="Volume vs 20D" value={`${metrics.volumeRatioVs20d.toFixed(2)}×`} />
              <MetricRow label="20D turnover" value={formatCompactPeso(research.avgTurnover20dPhp)} />
              <MetricRow
                label="Trailing dividend"
                value={
                  metrics.trailingDividendPerShare
                    ? `${formatPrice(metrics.trailingDividendPerShare)} · ${
                        metrics.trailingDividendYieldPct?.toFixed(2) ?? '—'
                      }%`
                    : 'No payout in 12m'
                }
              />
              <MetricRow
                label="Last ex-date"
                value={metrics.lastExDividendDate ? formatDate(metrics.lastExDividendDate) : '—'}
              />
            </div>
          ) : (
            <p className="mt-2 text-xs text-fg-subtle">No OHLCV snapshot for this name yet.</p>
          )}
        </section>

        <section className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <h4 className="type-eyebrow text-fg-subtle">Disclosures &amp; flags</h4>
          {!research.passesLiquidityFloor ? (
            <p className="mt-2 rounded border border-amber-400/40 bg-amber-400/10 px-2 py-1 text-2xs text-amber-400">
              Below the desk&apos;s 20-day turnover floor — the pipeline would not rank this name.
            </p>
          ) : null}
          {!research.inCatalog ? (
            <p className="mt-2 text-2xs text-fg-subtle">
              Not in the ranked catalog; researched on demand from the live feed.
            </p>
          ) : null}
          {research.trapFlags.length > 0 ? (
            <p className="mt-2 type-eyebrow text-amber-400">
              Flags: {research.trapFlags.join(' · ')}
            </p>
          ) : null}
          <ul className="mt-2 space-y-1.5">
            {research.disclosures.length === 0 ? (
              <li className="text-xs text-fg-subtle">No disclosures retrieved for this name.</li>
            ) : (
              research.disclosures.slice(0, 8).map((disclosure) => (
                <li key={`${disclosure.date}-${disclosure.headline}`} className="text-xs leading-snug">
                  <span className="tabular text-fg-subtle">{formatDate(disclosure.date)}</span>{' '}
                  <span className="text-fg-muted">{disclosure.headline}</span>{' '}
                  <span className="type-eyebrow text-fg-faint">
                    {disclosure.category} · {disclosure.source}
                  </span>
                </li>
              ))
            )}
          </ul>
        </section>
      </div>

      </OptionalDisclosure>

      <div className="mt-3 rounded-xl border border-rose-400/30 bg-rose-400/5 p-3">
        <p className="type-eyebrow text-rose-400/80">
          {simple ? 'What would prove this wrong' : 'Bear case · pre-mortem'}
        </p>
        <p className="mt-1.5 type-body measure text-fg-muted">
          {research.bearCase || 'No pre-mortem returned for this name.'}
        </p>
      </div>

      {research.aiThesis ? (
        <div className="mt-3 rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <p className="type-eyebrow text-fg-subtle">Desk note · AI prose</p>
          <p className="mt-1.5 type-body measure text-fg-muted">{research.aiThesis}</p>
          <p className="mt-1.5 text-2xs text-fg-faint">
            Prose only: it never owns a level, a size or a verdict. Everything above is deterministic.
          </p>
        </div>
      ) : null}
    </div>
  );
}


/**
 * On-demand research for any PSE ticker, ranked or not. The API prices the plan from the
 * live series; the dialog only renders the result and can hand the ticket to the blotter.
 */
export function ResearchDialog({ open, onOpenChange, horizon, onTakeTrade, viewMode = 'simple' }: ResearchDialogProps) {
  const simple = viewMode === 'simple';
  const [draft, setDraft] = useState('');
  const [query, setQuery] = useState<string | null>(null);
  const [catalog, setCatalog] = useState<CatalogStock[]>([]);
  const [research, setResearch] = useState<TickerResearch | null>(null);
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);
  // Two scenarios, one dialog: a fresh entry, and a position the reader already holds. The second
  // tab sends the reader's own cost to the server, which grades the position against its own ladder.
  const [tab, setTab] = useState<'buy' | 'own'>('buy');
  const [averageCost, setAverageCost] = useState('');
  const [sharesHeld, setSharesHeld] = useState('');
  const [holding, setHolding] = useState<ResearchHolding | null>(null);

  // One catalog fetch per dialog session: static ticker + legal-name metadata, no prices.
  // The shell normally warms it while idle, so this is usually a cache hit.
  useEffect(() => {
    if (!open || catalog.length > 0) {
      return;
    }

    const cached = getSnapshot<CatalogStock[]>(queryKeys.researchCatalog);

    if (cached) {
      setCatalog(cached);
      return;
    }

    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      void prefetchQuery({
        key: queryKeys.researchCatalog,
        staleTimeMs: queryStaleTime.researchCatalog,
        signal: controller.signal,
        fetcher: (signal) => researchApi.catalog(signal),
      }).then(() => {
        const warmed = getSnapshot<CatalogStock[]>(queryKeys.researchCatalog);

        if (warmed) {
          setCatalog(warmed);
        }
      });
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [open, catalog.length]);

  const suggestions = useMemo(
    () => (simple ? searchCatalog(catalog, draft) : []),
    [catalog, draft, simple],
  );
  const showSuggestions = suggestions.length > 0 && draft.trim().length > 0 && suggestions[0].ticker !== draft.trim().toUpperCase();

  useEffect(() => {
    if (open) {
      return;
    }

    setQuery(null);
    setResearch(null);
    setStatus('idle');
    setError(null);
    setHolding(null);
  }, [open]);

  useEffect(() => {
    if (!open || !query) {
      return;
    }

    const key = queryKeys.researchItem(horizon, query, holding ?? undefined);
    const cached = getSnapshot<TickerResearch>(key);

    // Pricing is a live-feed read, so it is never prefetched — but asking for the same
    // ticker again inside the stale window is a repeat of a request the reader just made.
    setResearch(cached ?? null);
    setStatus(cached ? 'ready' : 'loading');
    setError(null);

    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchQuery({
        key,
        staleTimeMs: queryStaleTime.researchItem,
        signal: controller.signal,
        fetcher: (signal) => researchApi.get(query, horizon, signal, holding ?? undefined),
      })
        .then((result) => {
          if (controller.signal.aborted) {
            return;
          }

          setResearch(result);
          setStatus('ready');
        })
        .catch((caught: unknown) => {
          if (controller.signal.aborted) {
            return;
          }

          setError(describeFriendlyError(caught).message);
          setStatus('error');
        });
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [open, query, horizon, holding]);

  const ticket = research?.orderTicket ?? null;
  const canTake = research !== null && ticket !== null && ticket.shares > 0;
  const costValue = Number(averageCost);
  const canApplyHolding = averageCost.trim().length > 0 && Number.isFinite(costValue) && costValue > 0;

  /** Sends the reader's own cost basis; the server owns every number that comes back. */
  function applyHolding(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();

    if (!canApplyHolding) {
      return;
    }

    const size = Number(sharesHeld);
    setHolding({
      averageCost: costValue,
      shares: Number.isFinite(size) && size > 0 ? Math.floor(size) : undefined,
    });
  }

  function takeTrade() {
    if (!research || !ticket || ticket.shares <= 0) {
      return;
    }

    onTakeTrade({
      ticker: research.ticker,
      companyName: research.companyName,
      sector: research.sector,
      horizon,
      edgeVerdict: research.edgeVerdict as EdgeVerdict,
      entryPrice: research.plan.entryPrice,
      stopLoss: research.plan.stopLoss,
      targetPrice: research.plan.targetPrice,
      shares: ticket.shares,
    });
    onOpenChange(false);
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        <DialogStickyHeader>
          <div className="min-w-0">
            <DialogTitle className="text-lg font-semibold text-fg">
              {simple ? 'Check Any PSE Stock' : 'Research a ticker'}
            </DialogTitle>
            <DialogDescription className="mt-1 max-w-2xl type-body measure text-fg-muted">
              {simple
                ? 'Type the company name you know — Jollibee, Meralco, PLDT — or its PSE symbol. The model prices the entry, the target and the cut-loss on that name and reports what it would size.'
                : 'Any PSE symbol, ranked or not. The desk pulls the live OHLCV series plus the latest PSE Edge disclosures and prices the ATR plan, the odds and the board-lot ticket on the server.'}
            </DialogDescription>
          </div>
          <DialogClose asChild>
            <Button variant="ghost">Close</Button>
          </DialogClose>
        </DialogStickyHeader>

        <form
          className="mt-3"
          onSubmit={(event) => {
            event.preventDefault();
            const symbol = draft.trim();
            setQuery(symbol.length > 0 ? symbol : null);
          }}
        >
          <div className="flex flex-wrap items-center gap-2">
            <input
              type="text"
              value={draft}
              onChange={(event) => setDraft(event.target.value)}
              placeholder="Company or symbol — e.g. Jollibee, Meralco, JFC"
              aria-label="Company name or PSE ticker"
              autoComplete="off"
              spellCheck={false}
              className={cn(inputClass, 'w-full text-xs sm:w-72 sm:max-w-xs')}
            />
            <Button
              type="submit"
              variant="primary"
              busy={status === 'loading'}
              disabled={draft.trim().length === 0}
              className="flex-1 sm:flex-none"
            >
              {simple ? 'Check this stock' : 'Research'}
            </Button>
            {research && tab === 'buy' ? (
              <Button
                variant="secondary"
                onClick={takeTrade}
                disabled={!canTake}
                className="flex-1 sm:flex-none"
              >
                {simple ? 'Practise This Trade' : `Record in blotter ${ticket ? ` · ${formatInteger(ticket.shares)} sh` : ''}`}
              </Button>
            ) : null}
          </div>

          {showSuggestions ? (
            <ul
              className="mt-2 w-full max-w-md overflow-hidden rounded-lg border border-line/80 bg-ink-850"
              aria-label="Matching PSE companies"
            >
              {suggestions.map((stock) => (
                <li key={stock.ticker}>
                  <button
                    type="button"
                    onClick={() => {
                      setDraft(stock.ticker);
                      setQuery(stock.ticker);
                    }}
                    className="flex w-full items-baseline justify-between gap-3 px-3 py-1.5 text-left transition hover:bg-ink-800"
                  >
                    <span className="text-xs font-medium text-fg">
                      {companyLabel(stock.ticker, stock.companyName)}
                    </span>
                    <span className="font-mono text-2xs text-fg-subtle">{stock.ticker}</span>
                  </button>
                </li>
              ))}
            </ul>
          ) : null}

          {simple ? (
            <div className="mt-2 flex flex-wrap items-center gap-1.5">
              <span className="type-eyebrow text-fg-faint">Popular</span>
              {POPULAR_PSE_STOCKS.map((stock) => (
                <button
                  key={stock.ticker}
                  type="button"
                  onClick={() => {
                    setDraft(stock.ticker);
                    setQuery(stock.ticker);
                  }}
                  className="rounded-full border border-line/80 bg-ink-850 px-2.5 py-1 text-2xs font-medium text-fg-muted transition hover:border-mint-400/40 hover:text-mint-400"
                >
                  {stock.label}
                </button>
              ))}
            </div>
          ) : null}
        </form>

        {/* Simple mode runs two model scenarios in one dialog: a fresh entry, and one already held. */}
        <Tabs
          value={tab}
          className="mt-3"
          onValueChange={(value) => {
            const next = value === 'own' ? 'own' : 'buy';
            setTab(next);
            // Going back to the buy plan drops the reader's cost basis so the server prices
            // the plain plan again instead of reusing the position query.
            if (next === 'buy' && holding) {
              setHolding(null);
            }
          }}
        >
          <TabsList>
            <TabsTrigger value="buy">{simple ? 'Entry scenario' : 'New entry'}</TabsTrigger>
            <TabsTrigger value="own">{simple ? 'Holding scenario' : 'Existing position'}</TabsTrigger>
          </TabsList>
        </Tabs>

        {tab === 'own' ? (
          <form className="mt-2 rounded-xl border border-line/70 bg-ink-850/60 p-3" onSubmit={applyHolding}>
            <p className="type-eyebrow text-fg-subtle">
              Your average cost
            </p>
            <div className="mt-2 flex flex-wrap items-end gap-2">
              <label className="flex flex-col gap-1 text-2xs text-fg-subtle">
                Average price paid
                <span className="flex items-center gap-1 rounded-lg border border-line/80 bg-ink-900 px-2 py-1">
                  <span aria-hidden className="text-fg-subtle">
                    ₱
                  </span>
                  <input
                    type="number"
                    inputMode="decimal"
                    min="0"
                    step="0.01"
                    value={averageCost}
                    onChange={(event) => setAverageCost(event.target.value)}
                    aria-label="Average price you paid per share"
                    className="tabular w-24 bg-transparent text-xs text-fg outline-none"
                  />
                </span>
              </label>
              <label className="flex flex-col gap-1 text-2xs text-fg-subtle">
                Shares held (optional)
                <input
                  type="number"
                  inputMode="numeric"
                  min="0"
                  step="1"
                  value={sharesHeld}
                  onChange={(event) => setSharesHeld(event.target.value)}
                  aria-label="Shares you currently hold"
                  placeholder="not needed for the verdict"
                  className={cn(inputClass, 'w-40')}
                />
              </label>
              <Button type="submit" variant="primary" busy={status === 'loading'} disabled={!canApplyHolding}>
                {simple ? 'Check my position' : 'Grade position'}
              </Button>
              {holding ? (
                <button
                  type="button"
                  onClick={() => {
                    setHolding(null);
                    setAverageCost('');
                    setSharesHeld('');
                  }}
                  className="pb-1 text-2xs text-fg-subtle underline decoration-line underline-offset-2 transition hover:text-fg-muted"
                >
                  Clear my numbers
                </button>
              ) : null}
            </div>
            <p className="mt-1.5 type-caption text-fg-faint">
              {simple
                ? 'The desk compares your cost with today’s price after broker fees, then reports where the position sits on the same four-step ladder the entry plan uses.'
                : 'The server grades the position against its own stop / target / trail ladder. Shares only scale the peso result; they never change the verdict.'}
            </p>
          </form>
        ) : null}

        {status === 'loading' ? (
          <p className="mt-4 text-xs text-fg-subtle">
            {simple ? `Looking up ${query ?? 'that stock'}…` : `Pricing ${query ?? 'the ticker'} from the live feed…`}
          </p>
        ) : null}

        {status === 'error' && !research ? (
          <p className="mt-4 rounded-lg border border-rose-400/30 bg-rose-400/5 px-3 py-2 type-body measure text-rose-400">
            {error ?? 'Could not research that ticker.'}
          </p>
        ) : null}

        {status === 'error' && research ? (
          <p className="mt-4 rounded-lg border border-amber-400/30 bg-amber-400/5 px-3 py-2 type-body measure text-amber-400">
            Showing the last priced plan — the live refresh failed: {error ?? 'unknown error'}
          </p>
        ) : null}

        {(status === 'ready' || status === 'error') && research ? (
          <ResearchBody research={research} horizon={horizon} simple={simple} focus={tab} />
        ) : null}



        {status === 'idle' ? (
          <div className="mt-4 rounded-xl border border-line/60 bg-ink-850/50 px-4 py-6 text-xs text-fg-subtle">
            <p className="type-eyebrow text-fg-subtle">What you get</p>
            <ul className="mt-2 space-y-1 leading-relaxed">
              {simple ? (
                <>
                  <li>· A buy price, a target price and a cut-loss price.</li>
                  <li>· How many shares to buy and roughly how much cash it needs.</li>
                  <li>· The chance it reaches the target, and the reason it could fail.</li>
                  <li>· Recent company filings that matter for the price.</li>
                </>
              ) : (
                <>
                  <li>· ATR-anchored entry, stop and target, snapped to the PSE tick grid.</li>
                  <li>· Calibrated win probability against the fee-adjusted break-even rate.</li>
                  <li>· The server order ticket: board lots, all-in cost, fee-paid risk, break-even stop.</li>
                  <li>· Liquidity floor, trap flags, recent disclosures and the written pre-mortem.</li>
                </>
              )}
            </ul>
            <p className="mt-2 text-2xs text-fg-faint">
              {simple
                ? 'You can check any listed PSE stock, even one the desk does not rank today.'
                : 'Names outside the ranked catalog are supported; they are flagged as unranked rather than refused.'}
            </p>
          </div>
        ) : null}

        <p className="mt-3 text-2xs text-fg-faint">
          {simple ? 'Holding period · ' : 'Horizon follows the terminal selection · '}
          {horizonMeta[horizon].holding}
          {simple ? ' · ' : ' holding window · '}
          {research ? `researched as of ${formatDate(research.asOfDate)}` : 'no ticker priced yet'}
        </p>
      </DialogContent>
    </Dialog>
  );
}
