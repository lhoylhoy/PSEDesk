import { useEffect, useState } from 'react';
import { signalsApi } from '../api/signalsApi';
import type { Horizon, SignalItem, TickerHistory } from '../types/signals';
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
  describeOrderGuide,
  describePlainVerdict,
  describeWinChance,
} from '../lib/plainAdvice';
import { companyLabel } from '../lib/pseNicknames';
import { fetchQuery, getSnapshot, queryKeys, queryStaleTime } from '../lib/queryCache';
import { formatProbability, formatR } from '../lib/riskSizing';
import type { ViewMode } from '../lib/viewMode';
import { OrderBox, PlanSteps } from './AdviceCardList';
import { DriftBadge } from './DriftBadge';
import { RiskSizerBar } from './RiskSizerBar';
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
import { Tooltip } from './ui/Tooltip';

interface TickerDetailDialogProps {
  item: SignalItem | null;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  horizon: Horizon;
  /** Hands the plan (with its server ticket) to the blotter. */
  onTakeTrade: (item: SignalItem) => void;
  /** Simple mode hides the dense sizing/indicator blocks inside drawers. */
  viewMode?: ViewMode;
}

/** In Simple mode the wrapped block becomes a drawer; in Pro it renders inline, unchanged. */

function PlanTile({ label, value, tone }: { label: string; value: string; tone?: string }) {
  return (
    <div className="rounded-xl border border-line/70 bg-ink-850/80 px-3 py-2">
      <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500">{label}</p>
      <p className={cn('tabular mt-1 text-sm font-semibold text-slate-100', tone)}>{value}</p>
    </div>
  );
}

function MetricCell({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-2 border-b border-line/50 py-1.5 last:border-0">
      <span className="text-[11px] text-slate-500">{label}</span>
      <span className="tabular text-[11px] font-medium text-slate-200">{value}</span>
    </div>
  );
}

function HistoryPanel({
  status,
  error,
  history,
}: {
  status: 'idle' | 'loading' | 'ready' | 'error';
  error: string | null;
  history: TickerHistory | null;
}) {
  if (status === 'loading') {
    return <p className="text-xs text-slate-500">Loading rank history…</p>;
  }

  if (status === 'error') {
    return <p className="text-xs text-rose-400">{error ?? 'Could not load history.'}</p>;
  }

  if (!history || history.points.length === 0) {
    return <p className="text-xs text-slate-500">First appearance in the ranked list — no prior runs.</p>;
  }

  return (
    <div>
      <div className="flex h-24 items-end gap-1.5">
        {history.points.map((point) => {
          const height = `${Math.max(12, ((11 - Math.min(point.rank, 10)) / 10) * 100)}%`;

          return (
            <Tooltip
              key={`${point.runDate}-${point.rank}`}
              side="top"
              label={
                <span>
                  {formatDate(point.runDate)} · rank #{point.rank} · {point.driftStatus} · conviction{' '}
                  {point.convictionScore}
                </span>
              }
            >
              <div className="flex h-full flex-1 flex-col items-center justify-end gap-1" tabIndex={0}>
                <span className="tabular text-[9px] text-slate-500">{point.rank}</span>
                <div
                  className={cn(
                    'w-full rounded-t',
                    point.driftStatus === 'NEW'
                      ? 'bg-sky-400/60'
                      : point.driftStatus === 'UPGRADED'
                        ? 'bg-mint-400/70'
                        : point.driftStatus === 'DOWNGRADED'
                          ? 'bg-amber-400/70'
                          : 'bg-slate-600',
                  )}
                  style={{ height }}
                />
              </div>
            </Tooltip>
          );
        })}
      </div>
      <ul className="mt-3 space-y-1.5">
        {history.points.map((point) => (
          <li
            key={`row-${point.runDate}`}
            className="flex items-center justify-between gap-3 rounded-lg border border-line/60 bg-ink-850/60 px-2.5 py-1.5"
          >
            <span className="text-[11px] text-slate-400">{formatDate(point.runDate)}</span>
            <span className="tabular text-[11px] text-slate-300">#{point.rank}</span>
            <DriftBadge status={point.driftStatus} previousRank={point.previousRank} />
            <span className="tabular text-[11px] text-slate-500">
              {formatPrice(point.entryPrice)} → {formatPrice(point.targetPrice)}
            </span>
            <span className="tabular text-[11px] text-slate-500">
              {formatProbability(point.winProbability)} · {formatR(point.expectedValueR)}
            </span>
            <span
              className={cn(
                'tabular text-[11px]',
                (point.realizedNetR ?? 0) > 0
                  ? 'text-mint-400'
                  : point.realizedNetR != null
                    ? 'text-rose-400'
                    : 'text-slate-600',
              )}
            >
              {point.realizedNetR != null
                ? formatR(point.realizedNetR)
                : point.outcomeStatus === 'OPEN'
                  ? 'open'
                  : '—'}
            </span>
          </li>
        ))}
      </ul>
    </div>
  );
}

export function TickerDetailDialog({
  item,
  open,
  onOpenChange,
  horizon,
  onTakeTrade,
  viewMode = 'simple',
}: TickerDetailDialogProps) {
  const simple = viewMode === 'simple';
  const ticker = item?.ticker ?? null;
  const [history, setHistory] = useState<TickerHistory | null>(null);
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!open || !ticker) {
      return;
    }

    const key = queryKeys.tickerHistory(ticker, horizon);
    const cached = getSnapshot<TickerHistory>(key);

    // The terminal warms this read on hover/focus, so opening the sheet usually lands a
    // hit. Re-showing a ticker that was just inspected must not flash a skeleton.
    setHistory(cached ?? null);
    setStatus(cached ? 'ready' : 'loading');
    setError(null);

    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchQuery({
        key,
        staleTimeMs: queryStaleTime.signals,
        signal: controller.signal,
        fetcher: (signal) => signalsApi.tickerHistory(ticker, horizon, signal),
      })
        .then((result) => {
          if (controller.signal.aborted) {
            return;
          }

          setHistory(result);
          setStatus('ready');
        })
        .catch((caught: unknown) => {
          if (controller.signal.aborted) {
            return;
          }

          setError(caught instanceof Error ? caught.message : 'Could not load ticker history.');
          setStatus('error');
        });
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [open, ticker, horizon]);

  const metrics = item?.metrics ?? null;
  const guide = item ? describeOrderGuide(item) : null;

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        {item ? (
          <>
            <DialogStickyHeader>
              <div className="min-w-0 flex-1">
                <div className="flex flex-wrap items-center gap-2">
                  <DialogTitle className="text-lg font-semibold leading-tight text-white sm:text-xl">
                    {companyLabel(item.ticker, item.companyName)}
                  </DialogTitle>
                  <span className="rounded border border-line/70 bg-ink-850 px-1.5 py-0.5 font-mono text-[10px] tracking-wider text-slate-400">
                    {item.ticker}
                  </span>
                  <DriftBadge status={item.driftStatus} previousRank={item.previousRank} friendly={simple} />
                  <Tooltip label={verdictDescription(item.edgeVerdict)}>
                    <span tabIndex={0}>
                      <VerdictBadge verdict={item.edgeVerdict} friendly={simple} />
                    </span>
                  </Tooltip>
                </div>
                <DialogDescription className="mt-1 text-xs text-slate-400">
                  {simple
                    ? `${item.companyName} · ${item.sector} · #${item.rank} today · plan to hold about ${horizonMeta[horizon].holding}`
                    : `${item.companyName} · ${item.sector} · rank #${item.rank} · ${horizonMeta[horizon].label} book · ${horizonMeta[horizon].holding}`}
                </DialogDescription>
              </div>
              {/* Phone: the conviction figure and the two actions take their own rows and are
                  allowed to wrap, so a long “Record in blotter · 1,240 sh” label cannot push
                  the close button off a 320px sheet. */}
              <div className="flex w-full flex-wrap items-center justify-between gap-3 sm:w-auto sm:justify-end sm:gap-4">
                <div className="text-right">
                  <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                    {simple ? 'Confidence' : 'Conviction'}
                  </p>
                  <p className="tabular text-lg font-semibold text-mint-400">{item.convictionScore}</p>
                </div>
                <div className="flex flex-wrap items-center gap-2">
                  <Button
                    variant="primary"
                    disabled={item.orderTicket == null || item.orderTicket.shares <= 0}
                    onClick={() => onTakeTrade(item)}
                  >
                    {simple ? 'Practise This Trade' : `Record in blotter${item.orderTicket ? ` · ${formatInteger(item.orderTicket.shares)} sh` : ''}`}
                  </Button>
                  <DialogClose asChild>
                    <Button variant="ghost">Close</Button>
                  </DialogClose>
                </div>
              </div>
            </DialogStickyHeader>

            {simple && guide ? (
              <>
                <p className="mt-4 text-xs leading-relaxed text-slate-300">
                  {describePlainVerdict(item.edgeVerdict).explanation}{' '}
                  {describeWinChance(item.winProbability, item.breakEvenWinRate)}
                </p>
                <div className="mt-3 space-y-3">
                  <PlanSteps
                    entryPrice={item.entryPrice}
                    targetPrice={item.targetPrice}
                    stopLoss={item.stopLoss}
                  />
                  <OrderBox guide={guide} item={item} />
                </div>
              </>
            ) : null}

            <OptionalDisclosure enabled={simple} title="Sizing & odds">
            <dl className="mt-4 grid grid-cols-2 gap-2 xs:grid-cols-3 lg:grid-cols-6">
              <PlanTile label="Entry" value={formatPrice(item.entryPrice)} />
              <PlanTile label="Stop" value={formatPrice(item.stopLoss)} tone="text-rose-400" />
              <PlanTile label="Target" value={formatPrice(item.targetPrice)} tone="text-mint-400" />
              <PlanTile label="Net R:R" value={formatRatio(item.netRiskRewardRatio)} />
              <PlanTile
                label="P(win)"
                value={`${formatProbability(item.winProbability)} / ${formatProbability(item.breakEvenWinRate)}`}
                tone={item.winProbability >= item.breakEvenWinRate ? 'text-mint-400' : 'text-rose-400'}
              />
              <PlanTile
                label="Board lot"
                value={`${formatInteger(item.boardLotSize)} sh`}
                tone="text-slate-300"
              />
            </dl>
            <p className="mt-1.5 text-[10px] text-slate-600">
              {item.orderTicket
                ? `Server ticket: ${formatInteger(item.orderTicket.shares)} shares (${formatInteger(
                    item.orderTicket.lots,
                  )} lot${item.orderTicket.lots === 1 ? '' : 's'}) · ${formatPrice(
                    item.orderTicket.buyOutlayPhp,
                  )} all-in incl. the buy leg · ${formatPrice(item.orderTicket.netRiskPhp)} risk after both fees · break-even stop ${formatPrice(
                    item.orderTicket.breakEvenStopPrice,
                  )}.`
                : item.boardLotShares
                  ? `The model budget funds ${formatInteger(item.boardLotShares)} shares at the entry price, rounded down to the ${formatInteger(item.boardLotSize)}-share board lot.`
                  : 'Position sizing pending — no board lot fits the model budget at this price.'}{' '}
              Net R:R deducts the 0.690% round-trip fee (0.295% on the buy leg, 0.395% on the
              sell leg); P(win) is shown against the fee-adjusted break-even rate.
            </p>

            <RiskSizerBar item={item} className="mt-4" />

            {item.outcomeStatus !== 'OPEN' ? (
              <p className="mt-1.5 text-[10px] text-slate-500">
                Forward ledger: <span className="uppercase tracking-wider">{item.outcomeStatus.replaceAll('_', ' ')}</span>
                {item.outcomeDate ? ` on ${formatDate(item.outcomeDate)}` : ''}
                {item.outcomeExitPrice != null ? ` at ${formatPrice(item.outcomeExitPrice)}` : ''}
                {item.realizedNetR != null
                  ? ` → ${formatR(item.realizedNetR)} after fees`
                  : ' → no position held'}
                {item.barsHeld != null ? `, ${item.barsHeld} bars held` : ''}.
              </p>
            ) : null}
            </OptionalDisclosure>

            <div className="mt-4 rounded-xl border border-line/70 bg-ink-850/60 p-3">
              <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                {simple ? 'Why the desk likes it' : 'Desk thesis'}
              </p>
              <p className="mt-1.5 text-xs leading-relaxed text-slate-300">{item.thesis}</p>
              {item.deviationReason ? (
                <p className="mt-2 border-t border-line/60 pt-2 text-[11px] leading-relaxed text-amber-400/90">
                  {item.deviationReason}
                </p>
              ) : null}
            </div>

            <div className="mt-4 rounded-xl border border-rose-400/30 bg-rose-400/5 p-3">
              <p className="text-[10px] uppercase tracking-[0.18em] text-rose-400/80">
                {simple ? 'What would prove this wrong' : 'Bear case · pre-mortem'}
              </p>
              <p className="mt-1.5 text-xs leading-relaxed text-slate-300">
                {item.bearCase || 'No pre-mortem stored for this pick.'}
              </p>
              <p className="mt-1.5 text-[10px] text-slate-600">
                {simple
                  ? 'Written before the trade, not after. If this happens first, sell at the cut-loss price.'
                  : 'Written before the trade, not after: if this happens first, the plan is wrong and the stop is the exit.'}
              </p>
            </div>

            <OptionalDisclosure enabled={simple} title="Technical indicators, rank history & filings">
            <div className="mt-4 grid gap-4 lg:grid-cols-2">
              <section className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
                <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                  Technicals · close day
                </h3>
                {metrics ? (
                  <div className="mt-2">
                    <MetricCell
                      label="Close"
                      value={`${formatPrice(metrics.closePrice)} (${formatPercent(metrics.changePct)})`}
                    />
                    <MetricCell label="RSI 14" value={metrics.rsi14.toFixed(1)} />
                    <MetricCell label="ATR 14" value={formatPrice(metrics.atr14)} />
                    <MetricCell label="EMA 20" value={formatPrice(metrics.ema20)} />
                    <MetricCell label="SMA 50" value={formatPrice(metrics.sma50)} />
                    <MetricCell label="SMA 150" value={formatPrice(metrics.sma150)} />
                    <MetricCell label="SMA 200" value={formatPrice(metrics.sma200)} />
                    <MetricCell label="52-week high" value={formatPrice(metrics.week52High)} />
                    <MetricCell
                      label="Weinstein stage"
                      value={
                        metrics.weinsteinStage
                          ? metrics.weinsteinStage.replace('STAGE_', 'Stage ').replace('_', ' — ')
                          : '—'
                      }
                    />
                    <MetricCell label="Support" value={formatPrice(metrics.supportLevel)} />
                    <MetricCell label="Resistance" value={formatPrice(metrics.resistanceLevel)} />
                    <MetricCell label="Volume vs 20D" value={`${metrics.volumeRatioVs20d.toFixed(2)}×`} />
                    <MetricCell label="20D turnover" value={formatCompactPeso(metrics.avgTurnover20dPhp)} />
                    <MetricCell
                      label="Net foreign"
                      value={
                        metrics.netForeignTodayPhp === null
                          ? 'unreported'
                          : formatCompactPeso(metrics.netForeignTodayPhp)
                      }
                    />
                    <MetricCell
                      label="RS vs PSEi 63D"
                      value={formatPercent(metrics.relativeStrength63d)}
                    />
                    <MetricCell
                      label="ATR 14 % of price"
                      value={formatPercent(metrics.atrPct, 2, false)}
                    />
                    <MetricCell
                      label="Volatility contraction"
                      value={
                        metrics.volatilityContractionRatio != null
                          ? `${metrics.volatilityContractionRatio.toFixed(2)}× base range`
                          : '—'
                      }
                    />
                    <MetricCell
                      label="Historical hit rate"
                      value={formatProbability(metrics.empiricalWinRate)}
                    />
                    <MetricCell
                      label="Hit rate · D / M / Y"
                      value={`${formatProbability(metrics.empiricalWinRateDay)} / ${formatProbability(
                        metrics.empiricalWinRateMonth,
                      )} / ${formatProbability(metrics.empiricalWinRateYear)}`}
                    />
                    <MetricCell
                      label="Up/down volume 20D"
                      value={
                        metrics.upDownVolumeRatio20d != null
                          ? `${metrics.upDownVolumeRatio20d.toFixed(2)}×`
                          : '—'
                      }
                    />
                    <MetricCell
                      label="Chaikin money flow 20D"
                      value={
                        metrics.chaikinMoneyFlow20d != null
                          ? metrics.chaikinMoneyFlow20d.toFixed(2)
                          : '—'
                      }
                    />
                    <MetricCell
                      label="Trend efficiency 63D"
                      value={
                        metrics.trendEfficiency63d != null
                          ? `${(metrics.trendEfficiency63d * 100).toFixed(0)}%`
                          : '—'
                      }
                    />
                    <MetricCell
                      label="Beta vs PSEi 252D"
                      value={metrics.beta252d != null ? metrics.beta252d.toFixed(2) : '—'}
                    />
                    <MetricCell
                      label="Max drawdown 252D"
                      value={formatPercent(metrics.maxDrawdown252dPct, 1, false)}
                    />
                    <MetricCell
                      label="Tick spread"
                      value={formatPercent(metrics.tickSpreadRatePct, 2, false)}
                    />
                    <MetricCell
                      label="Trailing dividend"
                      value={
                        metrics.trailingDividendPerShare
                          ? `${formatPrice(metrics.trailingDividendPerShare)} · ${
                              metrics.trailingDividendYieldPct?.toFixed(2) ?? '—'
                            }%`
                          : 'No payout in 12m'
                      }
                    />
                    <MetricCell
                      label="Last ex-dividend"
                      value={metrics.lastExDividendDate ? formatDate(metrics.lastExDividendDate) : '—'}
                    />
                    <MetricCell
                      label="Session range"
                      value={
                        metrics.openPrice != null && metrics.highPrice != null && metrics.lowPrice != null
                          ? `${formatPrice(metrics.lowPrice)} – ${formatPrice(metrics.highPrice)}`
                          : '—'
                      }
                    />
                    <MetricCell
                      label="Quant pre-score"
                      value={`${metrics.quantPreScore} / ${metrics.netForeignTodayPhp === null ? '85' : '100'}`}
                    />
                  </div>
                ) : (
                  <p className="mt-2 text-xs text-slate-500">
                    No technical snapshot stored for this run.
                  </p>
                )}
              </section>

              <section className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
                <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                  Rank trajectory
                </h3>
                <div className="mt-2">
                  <HistoryPanel status={status} error={error} history={history} />
                </div>
              </section>
            </div>

            <section className="mt-4 rounded-xl border border-line/70 bg-ink-850/60 p-3">
              <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                Recent disclosures
              </h3>
              {item.disclosures.length === 0 ? (
                <p className="mt-2 text-xs text-slate-500">
                  PSE Edge published no headline matching this ticker for the session.
                </p>
              ) : (
                <ul className="mt-2 space-y-1.5">
                  {item.disclosures.map((disclosure) => (
                    <li
                      key={`${disclosure.date}-${disclosure.headline}`}
                      className="flex flex-wrap items-baseline gap-2 text-[11px] text-slate-400"
                    >
                      <span className="tabular text-slate-500">{formatDate(disclosure.date)}</span>
                      <span className="rounded border border-line/70 px-1 py-0.5 text-[9px] uppercase tracking-wider text-slate-500">
                        {disclosure.category}
                      </span>
                      {disclosure.trapFlag && disclosure.trapFlag !== 'NONE' ? (
                        <span className="rounded border border-amber-400/40 bg-amber-400/10 px-1 py-0.5 text-[9px] uppercase tracking-wider text-amber-400">
                          {disclosure.trapFlag.replaceAll('_', ' ')}
                        </span>
                      ) : null}
                      <span className="text-slate-300">{disclosure.headline}</span>
                      <span className="text-[10px] text-slate-600">{disclosure.source}</span>
                    </li>
                  ))}
                </ul>
              )}
            </section>

            <section className="mt-4 rounded-xl border border-line/70 bg-ink-850/60 p-3">
              <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                Playbook citations
              </h3>
              {item.citations && item.citations.length > 0 ? (
                <ul className="mt-2 flex flex-wrap gap-1.5">
                  {item.citations.map((citation) => (
                    <li
                      key={citation}
                      className="rounded border border-mint-400/30 bg-mint-400/5 px-1.5 py-0.5 font-mono text-[10px] text-mint-400"
                    >
                      {citation}
                    </li>
                  ))}
                </ul>
              ) : (
                <p className="mt-2 text-xs text-slate-500">
                  The model did not cite a retrieved playbook section for this name.
                </p>
              )}
              <p className="mt-2 text-[10px] leading-relaxed text-slate-600">
                Every reference above was matched against the knowledge base at retrieval time; fabricated
                citations are dropped server-side.
              </p>
            </section>
            </OptionalDisclosure>

            <p className="mt-4 text-[10px] leading-relaxed text-slate-600">
              Research output only. Levels are rounded to the PSE board lot and tick table; how much to risk is the reader's own decision.
            </p>
          </>
        ) : (
          <DialogTitle className="text-sm text-slate-200">Ticker details</DialogTitle>
        )}
      </DialogContent>
    </Dialog>
  );
}
