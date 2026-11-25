import { useEffect, useState } from 'react';
import { signalsApi } from '../api/signalsApi';
import type { Horizon, SignalScorecard } from '../types/signals';
import { cn } from '../lib/cn';
import { formatDate, formatInteger, formatPrice } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import { describeOutcomeStatus } from '../lib/plainAdvice';
import { fetchQuery, getSnapshot, queryKeys, queryStaleTime } from '../lib/queryCache';
import { formatProbability, formatR } from '../lib/riskSizing';
import type { ViewMode } from '../lib/viewMode';
import { VerdictBadge } from './VerdictBadge';
import { BacktestPanel } from './BacktestPanel';
import { ModelTrainerPanel } from './ModelTrainerPanel';
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

interface ScorecardDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  horizon: Horizon;
  /** Simple mode renames the ledger and folds calibration depth into a drawer. */
  viewMode?: ViewMode;
}

function KpiTile({
  label,
  value,
  detail,
  tone,
  hint,
}: {
  label: string;
  value: string;
  detail?: string;
  tone?: string;
  hint?: string;
}) {
  const tile = (
    <div
      className="rounded-xl border border-line/70 bg-ink-850/80 px-3 py-2"
      tabIndex={hint ? 0 : undefined}
    >
      <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500">{label}</p>
      <p className={cn('tabular mt-1 text-sm font-semibold text-slate-100', tone)}>{value}</p>
      {detail ? <p className="tabular mt-0.5 text-[10px] text-slate-500">{detail}</p> : null}
    </div>
  );

  return hint ? <Tooltip label={hint}>{tile}</Tooltip> : tile;
}

function outcomeTone(status: string): string {
  if (status === 'WIN_TARGET' || status === 'EXPIRED_WIN') {
    return 'text-mint-400';
  }

  if (status === 'LOSS_STOP' || status === 'EXPIRED_LOSS') {
    return 'text-rose-400';
  }

  return 'text-slate-400';
}

/**
 * A graded pick with no realized R was never filled — the limit never traded — so it carries no
 * profit tone. Painting it rose would read as a loss that never happened.
 */
function realizedTone(value: number | null): string {
  if (value == null) {
    return 'text-slate-400';
  }

  if (value > 0) {
    return 'text-mint-400';
  }

  return value < 0 ? 'text-rose-400' : 'text-slate-300';
}

export function ScorecardDialog({ open, onOpenChange, horizon, viewMode = 'simple' }: ScorecardDialogProps) {
  const simple = viewMode === 'simple';
  const [scorecard, setScorecard] = useState<SignalScorecard | null>(null);
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);
  const [view, setView] = useState<'scorecard' | 'replay' | 'trainer'>('scorecard');

  useEffect(() => {
    if (!open) {
      return;
    }

    setView('scorecard');
  }, [open]);

  useEffect(() => {
    if (!open) {
      return;
    }

    const key = queryKeys.scorecard(horizon);
    const cached = getSnapshot<SignalScorecard>(key);

    // Re-opening a sheet the terminal already graded paints instantly; the revalidation
    // still runs below, inside the same stale window rule the shell uses.
    if (cached) {
      setScorecard(cached);
      setStatus('ready');
    } else {
      setStatus('loading');
    }

    setError(null);

    // StrictMode remounts would otherwise abort a request that already reached the API,
    // which the server logs as a 499. Deferring one macrotask lets cleanup win instead.
    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchQuery({
        key,
        staleTimeMs: queryStaleTime.scorecard,
        signal: controller.signal,
        fetcher: (signal) => signalsApi.scorecard(horizon, signal),
      })
        .then((result) => {
          if (controller.signal.aborted) {
            return;
          }

          setScorecard(result);
          setStatus('ready');
        })
        .catch((caught: unknown) => {
          if (controller.signal.aborted) {
            return;
          }

          setError(caught instanceof Error ? caught.message : 'Could not load the scorecard.');
          setStatus('error');
        });
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [open, horizon]);

  const meta = horizonMeta[horizon];
  const graded = scorecard !== null && scorecard.resolvedCount > 0;
  // Picks persisted before the odds migration have no forecast, so the server keeps them out of
  // Brier/buckets. An empty calibration table therefore means "nothing to grade", not "0.000".
  const calibrationSamples =
    scorecard?.calibration.reduce((total, bucket) => total + bucket.sampleCount, 0) ?? 0;
  const calibrated = calibrationSamples > 0;

  const labels = simple
    ? {
        graded: 'Picks graded',
        winRate: 'Hit rate',
        expectancy: 'Average result',
        profitFactor: 'Profit factor',
        brier: 'Forecast accuracy',
        recent: 'Recent results',
      }
    : {
        graded: 'Graded',
        winRate: 'Win rate',
        expectancy: 'Expectancy',
        profitFactor: 'Profit factor',
        brier: 'Brier score',
        recent: 'Recent graded picks',
      };

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="lg">
        <DialogStickyHeader>
          <div className="min-w-0">
            <DialogTitle className="text-lg font-semibold text-white">
              {simple ? 'Past Results & Track Record' : 'Desk scorecard'}
            </DialogTitle>
            <DialogDescription className="mt-1 max-w-2xl text-xs leading-relaxed text-slate-400">
              {simple
                ? `Every pick the desk made on the ${meta.friendlyTab.toLowerCase()} list, checked against what the price actually did afterwards. Nothing is back-filled or simulated.`
                : `${meta.label} book · every persisted pick graded against the ${meta.holding} of OHLC sessions that followed it. Win rate, expectancy and Brier calibration come from the stored ledger, never from the model's own claims.`}
            </DialogDescription>
          </div>
          <DialogClose asChild>
            <Button variant="ghost">Close</Button>
          </DialogClose>
        </DialogStickyHeader>

        <div className="mt-3">
          <Tabs value={view} onValueChange={(value) => setView(value as 'scorecard' | 'replay' | 'trainer')}>
            <TabsList className="max-w-full">
              <TabsTrigger value="scorecard">{simple ? 'Past Results' : 'Forward scorecard'}</TabsTrigger>
              <TabsTrigger value="replay">{simple ? 'Backtest (simulation)' : 'Walk-forward replay'}</TabsTrigger>
              <TabsTrigger value="trainer">{simple ? 'Desk Robot' : 'Learned edge'}</TabsTrigger>
            </TabsList>
          </Tabs>
        </div>

        {view === 'scorecard' ? (
          <>
        {status === 'loading' ? (
          <p className="mt-4 text-xs text-slate-500">Grading the ledger…</p>
        ) : null}

        {status === 'error' ? (
          <p className="mt-4 text-xs text-rose-400">{error ?? 'Could not load the scorecard.'}</p>
        ) : null}

        {status === 'ready' && scorecard ? (
          <>
            <dl className="mt-4 grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-5">
              <KpiTile
                label={labels.graded}
                value={`${formatInteger(scorecard.resolvedCount)} / ${formatInteger(scorecard.totalPicks)}`}
                detail={`${formatInteger(scorecard.openCount)} still open`}
                hint={
                  simple
                    ? 'Every pick is graded once its holding period finishes. Open picks have not had time to finish yet.'
                    : undefined
                }
              />
              <KpiTile
                label={labels.winRate}
                value={graded ? formatProbability(scorecard.winRate) : '—'}
                detail={simple ? 'hit target or ended higher' : 'target hit or positive expiry'}
                tone={graded ? (scorecard.winRate >= 0.5 ? 'text-mint-400' : 'text-amber-400') : 'text-slate-500'}
                hint={
                  simple
                    ? 'Out of every 100 picks, how many made money. Anything above half is decent; the desk still needs the winners to be bigger than the losers.'
                    : undefined
                }
              />
              <KpiTile
                label={labels.expectancy}
                value={graded ? formatR(scorecard.expectancyR) : '—'}
                detail={
                  simple
                    ? `average per trade ${graded ? formatR(scorecard.averageRealizedNetR) : '—'} after fees`
                    : `avg ${graded ? formatR(scorecard.averageRealizedNetR) : '—'} after fees`
                }
                tone={graded && scorecard.expectancyR > 0 ? 'text-mint-400' : 'text-rose-400'}
                hint={
                  simple
                    ? 'The typical result of one trade, measured in risk units. Positive means the plan is expected to make money over many trades.'
                    : 'Mean realized R per graded pick; positive expectancy is required before any edge claim is made.'
                }
              />
              <KpiTile
                label={labels.profitFactor}
                value={graded ? scorecard.profitFactor.toFixed(2) : '—'}
                detail={simple ? 'wins vs losses · above 1.00 is good' : 'win R ÷ loss R'}
                hint={
                  simple
                    ? 'Total profit from winners divided by total losses. Above 1.00 means the winners covered every losing trade after broker fees.'
                    : 'Total realized R from winners divided by total realized R from losers. Above 1.00 means the book paid for its losses after the 0.690% round trip.'
                }
              />
              <KpiTile
                label={labels.brier}
                value={calibrated ? scorecard.brierScore.toFixed(3) : '—'}
                detail={
                  calibrated
                    ? simple
                      ? 'lower is better'
                      : `lower is better · n=${formatInteger(calibrationSamples)}`
                    : simple
                      ? 'not enough graded picks yet'
                      : 'no forecasted pick graded yet'
                }
                tone={calibrated && scorecard.brierScore < 0.25 ? 'text-mint-400' : 'text-slate-200'}
                hint={
                  simple
                    ? 'How close the desk’s win-chance guesses were to what actually happened. 0.250 is the score of always guessing 50/50, so lower than that means the guess is adding information.'
                    : 'Mean squared error of the predicted win probability against the 0/1 outcome. 0.25 is the score of a permanent 50/50 guess; below that the calibration is adding information. Picks stored before the odds migration carry no probability and are excluded.'
                }
              />
            </dl>

            {simple ? (
              <p className="mt-2 text-[11px] leading-relaxed text-slate-500">
                Short version: the hit rate says how often the desk was right, the average result says how much it
                made per trade after fees, and the profit factor says whether the winners outweighed the losers. All
                three are measured on what the price actually did afterwards — never on what the model claims.
              </p>
            ) : null}

            {!graded ? (
              <p className="mt-4 rounded-xl border border-line/70 bg-ink-850/60 px-3 py-2.5 text-[11px] leading-relaxed text-slate-400">
                {simple
                  ? `Nothing has finished yet for this list. A pick is only graded once its ${meta.holding} holding window has passed, so this page fills in over time — nothing is back-filled or simulated.`
                  : `No ${meta.label.toLowerCase()} pick has been graded yet. The resolver waits for the horizon window to elapse, so this table fills in as the store accumulates sessions. Nothing is back-filled or simulated.`}
              </p>
            ) : null}
            {graded && !calibrated ? (
              <p className="mt-4 rounded-xl border border-line/70 bg-ink-850/60 px-3 py-2.5 text-[11px] leading-relaxed text-slate-400">
                The graded picks here were stored before the odds migration, so they carry no forecast:
                their realized R counts, but they are excluded from the Brier score and the calibration
                buckets. The next forecasted run that resolves will start filling the table.
              </p>
            ) : null}
            <OptionalDisclosure enabled={simple} title="Calibration & verdict breakdown">
            <div className="mt-4 grid gap-4 lg:grid-cols-2">
              <section className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
                <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                  Calibration · predicted vs realized
                </h3>
                {scorecard.calibration.length === 0 ? (
                  <p className="mt-2 text-xs text-slate-500">
                    Calibration needs graded picks in at least one probability bucket. Picks stored
                    before the odds migration carry no probability and are excluded.
                  </p>
                ) : (
                  <table className="mt-2 w-full border-collapse text-left">
                    <thead>
                      <tr className="text-[9px] uppercase tracking-[0.16em] text-slate-500">
                        <th className="py-1.5 font-medium">Bucket</th>
                        <th className="py-1.5 text-right font-medium">Predicted</th>
                        <th className="py-1.5 text-right font-medium">Realized</th>
                        <th className="py-1.5 text-right font-medium">n</th>
                      </tr>
                    </thead>
                    <tbody>
                      {scorecard.calibration.map((bucket) => {
                        const drift = bucket.actualWinPct - bucket.predictedWinPct;

                        return (
                          <tr key={bucket.label} className="border-t border-line/50">
                            <td className="py-1.5 text-[11px] text-slate-300">{bucket.label}</td>
                            <td className="tabular py-1.5 text-right text-[11px] text-slate-400">
                              {bucket.predictedWinPct.toFixed(1)}%
                            </td>
                            <td
                              className={cn(
                                'tabular py-1.5 text-right text-[11px] font-medium',
                                Math.abs(drift) <= 10 ? 'text-mint-400' : 'text-amber-400',
                              )}
                            >
                              {bucket.actualWinPct.toFixed(1)}%
                            </td>
                            <td className="tabular py-1.5 text-right text-[11px] text-slate-500">
                              {formatInteger(bucket.sampleCount)}
                            </td>
                          </tr>
                        );
                      })}
                    </tbody>
                  </table>
                )}
                <p className="mt-2 text-[10px] leading-relaxed text-slate-600">
                  A bucket is on target when realized and predicted agree within ten points; the Brier score
                  above summarizes the whole table.
                </p>
              </section>

              <section className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
                <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
                  Verdict breakdown · does TRADE beat WATCH?
                </h3>
                {scorecard.verdicts.length === 0 ? (
                  <p className="mt-2 text-xs text-slate-500">No verdicts recorded for this horizon yet.</p>
                ) : (
                  <ul className="mt-2 space-y-1.5">
                    {scorecard.verdicts.map((row) => (
                      <li
                        key={row.verdict}
                        className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-line/60 bg-ink-900/60 px-2.5 py-1.5"
                      >
                        <span className="flex items-center gap-2">
                          <VerdictBadge verdict={row.verdict} friendly={simple} />
                          <span className="tabular text-[10px] text-slate-500">
                            {formatInteger(row.resolvedCount)} / {formatInteger(row.count)} graded
                          </span>
                        </span>
                        <span className="flex items-center gap-3 text-[11px]">
                          <span className="tabular text-slate-300">
                            {row.resolvedCount > 0 ? formatProbability(row.winRate) : '—'} win
                          </span>
                          <span
                            className={cn(
                              'tabular font-semibold',
                              row.averageRealizedNetR > 0 ? 'text-mint-400' : 'text-rose-400',
                            )}
                          >
                            {row.resolvedCount > 0 ? formatR(row.averageRealizedNetR) : '—'} avg
                          </span>
                          <span className="tabular text-slate-500">{formatR(row.totalRealizedNetR)} total</span>
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
                <p className="mt-2 text-[10px] leading-relaxed text-slate-600">
                  If TRADE does not beat WATCH_ONLY over a real sample, the verdict rule is wrong and must be
                  revised — that is the point of grading the desk.
                </p>
              </section>
            </div>
            </OptionalDisclosure>

            <section className="mt-4 rounded-xl border border-line/70 bg-ink-850/60 p-3">
              <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">{labels.recent}</h3>
              {scorecard.recentResolved.length === 0 ? (
                <p className="mt-2 text-xs text-slate-500">Nothing has resolved yet.</p>
              ) : (
                <ul className="mt-2 space-y-1.5">
                  {scorecard.recentResolved.map((pick) => (
                    <li
                      key={`${pick.ticker}-${pick.runDate}`}
                      className="flex flex-wrap items-center justify-between gap-2 rounded-lg border border-line/60 bg-ink-900/60 px-2.5 py-1.5 text-[11px]"
                    >
                      <span className="flex items-center gap-2">
                        <span className="font-mono font-semibold tracking-wide text-slate-200">
                          {pick.ticker}
                        </span>
                        <VerdictBadge verdict={pick.edgeVerdict} friendly={simple} />
                        <span className="tabular text-slate-500">{formatDate(pick.runDate)}</span>
                      </span>
                      <span className="flex flex-wrap items-center gap-3">
                        <span className="tabular text-slate-500">
                          {formatPrice(pick.entryPrice)} →{' '}
                          {pick.outcomeExitPrice != null ? formatPrice(pick.outcomeExitPrice) : '—'}
                        </span>
                        <span
                          className={cn(
                            'tabular text-[10px] uppercase tracking-wider',
                            outcomeTone(pick.outcomeStatus),
                          )}
                        >
                          {describeOutcomeStatus(pick.outcomeStatus)}
                        </span>
                        <span
                          className={cn('tabular font-semibold', realizedTone(pick.realizedNetR))}
                        >
                          {formatR(pick.realizedNetR)}
                        </span>
                        <span className="tabular text-[10px] text-slate-600">
                          {pick.barsHeld != null ? `${pick.barsHeld}d held` : '—'} · P(win){' '}
                          {pick.winProbability > 0 ? formatProbability(pick.winProbability) : '—'}
                        </span>
                      </span>
                    </li>
                  ))}
                </ul>
              )}
            </section>
          </>
        ) : null}
          </>
        ) : null}

        {view === 'replay' ? <BacktestPanel horizon={horizon} viewMode={viewMode} /> : null}
        {view === 'trainer' ? <ModelTrainerPanel horizon={horizon} viewMode={viewMode} /> : null}
      </DialogContent>
    </Dialog>
  );
}
