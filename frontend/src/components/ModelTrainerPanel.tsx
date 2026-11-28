import { useCallback, useEffect, useState } from 'react';
import { signalsApi } from '../api/signalsApi';
import type { DeskMemorySummary, Horizon, TrainedModelSummary } from '../types/signals';
import { cn } from '../lib/cn';
import { formatDate, formatInteger, formatTimestamp } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import {
  fetchQuery,
  getSnapshot,
  invalidateQueries,
  queryKeys,
  queryStaleTime,
} from '../lib/queryCache';
import { formatProbability, formatR } from '../lib/riskSizing';
import type { ViewMode } from '../lib/viewMode';
import { Button } from './ui/Button';
import { Tooltip } from './ui/Tooltip';

interface ModelTrainerPanelProps {
  horizon: Horizon;
  viewMode?: ViewMode;
}

function Tile({
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
    <div className="rounded-xl border border-line/70 bg-ink-850/80 px-3 py-2" tabIndex={hint ? 0 : undefined}>
      <p className="type-eyebrow text-fg-subtle">{label}</p>
      <p className={cn('tabular mt-1 text-sm font-semibold text-fg', tone)}>{value}</p>
      {detail ? <p className="tabular mt-0.5 text-2xs text-fg-subtle">{detail}</p> : null}
    </div>
  );

  return hint ? <Tooltip label={hint}>{tile}</Tooltip> : tile;
}

function statusTone(status: string): string {
  if (status === 'LEARNED') {
    return 'border-mint-400/40 bg-mint-400/10 text-mint-400';
  }

  if (status === 'PRIOR_FALLBACK') {
    return 'border-amber-400/40 bg-amber-400/10 text-amber-400';
  }

  return 'border-line/80 bg-ink-800/80 text-fg-muted';
}

function statusLabel(status: string): string {
  if (status === 'LEARNED') {
    return 'Learned weights active';
  }

  if (status === 'PRIOR_FALLBACK') {
    return 'Fit rejected · prior in charge';
  }

  return 'Not trained yet';
}

/**
 * The self-training money manager, made auditable. The desk fits one logistic model per
 * horizon on its own ATR-barrier history, grades it on a purged held-out slice it never saw,
 * and only lets it tilt live odds when it beats the neutral prior. A rejected fit is shown on
 * purpose: the panel exists so nobody has to guess whether a model is really in charge.
 */
export function ModelTrainerPanel({ horizon, viewMode = 'simple' }: ModelTrainerPanelProps) {
  const simple = viewMode === 'simple';
  const meta = horizonMeta[horizon];
  const [models, setModels] = useState<TrainedModelSummary[]>([]);
  const [memory, setMemory] = useState<DeskMemorySummary | null>(null);
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState<'one' | 'all' | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const load = useCallback(
    async (signal?: AbortSignal, force = false) => {
      const [nextModels, nextMemory] = await Promise.all([
        fetchQuery({
          key: queryKeys.models,
          staleTimeMs: queryStaleTime.models,
          force,
          signal,
          fetcher: (next) => signalsApi.models(next),
        }),
        fetchQuery({
          key: queryKeys.deskMemory(horizon),
          staleTimeMs: queryStaleTime.deskMemory,
          force,
          signal,
          fetcher: (next) => signalsApi.deskMemory(horizon, next),
        }),
      ]);

      setModels(nextModels);
      setMemory(nextMemory);
    },
    [horizon],
  );

  useEffect(() => {
    // A repeat visit to the Trainer tab paints the report it already read instead of
    // blanking the panel to a spinner for a payload the cache still considers fresh.
    const cachedModels = getSnapshot<TrainedModelSummary[]>(queryKeys.models);
    const cachedMemory = getSnapshot<DeskMemorySummary>(queryKeys.deskMemory(horizon));

    if (cachedModels && cachedMemory) {
      setModels(cachedModels);
      setMemory(cachedMemory);
      setStatus('ready');
    } else {
      setStatus('loading');
    }

    setError(null);

    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      load(controller.signal)
        .then(() => {
          if (!controller.signal.aborted) {
            setStatus('ready');
          }
        })
        .catch((caught: unknown) => {
          if (controller.signal.aborted) {
            return;
          }

          setError(caught instanceof Error ? caught.message : 'Could not load the trainer report.');
          setStatus('error');
        });
    }, 0);

    return () => {
      window.clearTimeout(timer);
      controller.abort();
    };
  }, [load, horizon]);

  const current = models.find((model) => model.horizon === horizon) ?? null;

  async function retrain(scope: 'one' | 'all') {
    setBusy(scope);
    setError(null);
    setNotice(null);

    try {
      if (scope === 'one') {
        const summary = await signalsApi.train(horizon);
        setNotice(describeTraining(summary));
      } else {
        const summaries = await signalsApi.trainAll();
        const promoted = summaries.filter((summary) => summary.isActive).length;
        setNotice(
          `Refit all four books: ${formatInteger(promoted)} of ${formatInteger(summaries.length)} earned ` +
            'the right to price live picks. The rest stay on the structural prior.',
        );
      }

      // A refit rewrites both the model report and the graded ledger it learned from.
      invalidateQueries('signals:models');
      invalidateQueries('signals:memory:');
      await load(undefined, true);
      setStatus('ready');
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'The refit failed.');
    } finally {
      setBusy(null);
    }
  }

  if (status === 'loading') {
    return <p className="mt-4 text-xs text-fg-subtle">Reading the trainer report…</p>;
  }

  if (status === 'error') {
    return (
      <p className="mt-4 rounded-lg border border-rose-400/30 bg-rose-400/5 px-3 py-2 text-xs text-rose-400">
        {error ?? 'Could not load the trainer report.'}
      </p>
    );
  }

  const active = current?.isActive === true;

  return (
    <div className="mt-4">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <p className="max-w-2xl type-body measure text-fg-muted">
          {simple
            ? `This trains the desk’s local 12-factor statistical odds model (not the Ollama Cloud LLM) on the ${meta.friendlyTab.toLowerCase()} barrier history, then tests it on unseen trades. It only tilts win probabilities when it beats the baseline out of sample.`
            : `Local L2-regularised logistic residual model per horizon (separate from the Ollama Cloud LLM ranker), fitted on ATR-barrier history, standardised on training-slice means and embargoed by ${meta.holding} of forward bars. Weights go live only when the purged held-out slice clears the sample gate, beats both the neutral baseline and 0.250 coin-flip Brier, and yields positive policy expectancy.`}
        </p>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="primary" busy={busy === 'one'} onClick={() => retrain('one')}>
            {simple ? `Re-train ${meta.label.toLowerCase()} robot` : `Refit ${meta.label.toUpperCase()}`}
          </Button>
          <Button busy={busy === 'all'} onClick={() => retrain('all')}>
            Refit all four
          </Button>
        </div>
      </div>

      {notice ? (
        <p className="mt-3 rounded-lg border border-mint-400/30 bg-mint-400/5 px-3 py-2 type-body measure text-mint-400">
          {notice}
        </p>
      ) : null}

      {error ? (
        <p className="mt-3 rounded-lg border border-rose-400/30 bg-rose-400/5 px-3 py-2 type-body measure text-rose-400">
          {error}
        </p>
      ) : null}

      {current ? (
        <>
          <div className={cn('mt-3 flex flex-wrap items-center gap-2 rounded-xl border px-3 py-2', statusTone(current.status))}>
            <span className="type-eyebrow">
              {statusLabel(current.status)}
            </span>
            <span className="text-xs text-fg-muted">{current.summary}</span>
            {current.trainedAtUtc ? (
              <span className="tabular text-2xs text-fg-subtle">
                fitted {formatTimestamp(current.trainedAtUtc)}
              </span>
            ) : null}
          </div>

          <dl className="mt-3 grid grid-cols-2 gap-2 xs:grid-cols-3 lg:grid-cols-6">
            <Tile
              label={simple ? 'Picks it studied' : 'Train / held-out'}
              value={`${formatInteger(current.trainSampleCount)} / ${formatInteger(current.validationSampleCount)}`}
              detail={
                current.trainStartDate && current.validationEndDate
                  ? `${formatDate(current.trainStartDate)} → ${formatDate(current.validationEndDate)}`
                  : 'no window recorded'
              }
              hint="Chronological 70/30 split. The held-out slice is purged of any trade whose forward window had not cleared before it began, plus a one-window embargo, so the score cannot peek at the answer."
            />
            <Tile
              label={simple ? 'Forecast accuracy' : 'Held-out Brier'}
              value={current.trainSampleCount + current.validationSampleCount > 0 ? current.trainedBrierScore.toFixed(3) : '—'}
              detail={`vs ${current.baselineBrierScore.toFixed(3)} plain baseline`}
              tone={active ? 'text-mint-400' : 'text-fg'}
              hint="Lower is better. 0.25 is what a permanent 50/50 guess scores. The model is promoted only when its held-out Brier is lower than the neutral prior's on the same trades."
            />
            <Tile
              label={simple ? 'Hit rate it saw' : 'Held-out win rate'}
              value={current.validationSampleCount > 0 ? formatProbability(current.validationWinRate) : '—'}
              detail={`${formatR(current.validationExpectancyR)} avg per trade`}
              tone={current.validationExpectancyR > 0 ? 'text-mint-400' : 'text-rose-400'}
            />
            <Tile
              label="Profit factor"
              value={current.validationSampleCount > 0 ? current.validationProfitFactor.toFixed(2) : '—'}
              detail="win R ÷ loss R"
            />
            <Tile
              label={simple ? 'Bet size dial' : 'Kelly fraction'}
              value={current.optimalKellyFractionMultiplier > 0 ? `${current.optimalKellyFractionMultiplier.toFixed(2)}x` : '—'}
              detail="0.25x = neutral"
              hint="Learned full-Kelly fraction inside [0.15, 0.35], scored by log growth net of drawdown on the held-out trades. The live Quarter-Kelly stake is rescaled by this, so a worse-behaved model automatically stakes less."
            />
            <Tile
              label="Embargo"
              value={`${formatInteger(current.embargoBars)} bars`}
              detail="gap before held-out"
            />
          </dl>
        </>
      ) : null}

      {current && current.weights.length > 0 ? (
        <section className="mt-3 rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <h3 className="type-eyebrow text-fg-subtle">
            {simple ? 'What the robot pays attention to' : 'Learned factor weights · ranked by impact'}
          </h3>
          <ul className="mt-2 space-y-1">
            {current.weights.slice(0, simple ? 4 : 8).map((weight) => (
              <li key={weight.feature} className="rounded-lg border border-line/60 bg-ink-900/60 px-2.5 py-1.5">
                <div className="flex flex-wrap items-center justify-between gap-2 text-xs">
                  <span className="text-fg-muted">{weight.label}</span>
                  <span className="flex items-center gap-3">
                    <span
                      className={cn(
                        'tabular type-eyebrow',
                        weight.weight >= 0 ? 'text-mint-400' : 'text-rose-400',
                      )}
                    >
                      {weight.direction}
                    </span>
                    <span className="tabular text-fg-subtle">{weight.weight.toFixed(3)}</span>
                    <span className="tabular w-14 text-right text-fg-muted">
                      {weight.normalizedImpactPct.toFixed(1)}%
                    </span>
                  </span>
                </div>
                <div className="mt-1 h-1 overflow-hidden rounded-full bg-ink-700">
                  <div
                    className={cn('h-full', weight.weight >= 0 ? 'bg-mint-400/70' : 'bg-rose-400/70')}
                    style={{ width: `${Math.min(100, Math.abs(weight.normalizedImpactPct))}%` }}
                  />
                </div>
              </li>
            ))}
          </ul>
          <p className="mt-2 type-caption text-fg-faint">
            Impact is the share of total log-odds movement one standard deviation of that factor
            contributes. A negative weight means the factor lowers the odds of a target hit — the desk
            reads it, it does not relabel it.
          </p>
        </section>
      ) : null}
      <section className="mt-3 rounded-xl border border-line/70 bg-ink-850/60 p-3">
        <h3 className="type-eyebrow text-fg-subtle">
          {simple ? 'What has actually happened' : 'Desk memory · the graded ledger'}
        </h3>
        {!memory || memory.resolvedCount === 0 ? (
          <p className="mt-2 text-xs text-fg-subtle">
            Nothing has finished yet for this book, so there is no track record to learn from. The desk
            needs graded picks — and their outcomes — before a model can be fitted at all.
          </p>
        ) : (
          <>
            <p className="tabular mt-1 text-xs text-fg-muted">
              {formatInteger(memory.resolvedCount)} graded · {formatProbability(memory.winRate)} hit rate ·{' '}
              {formatR(memory.expectancyR)} average · Brier {memory.brierScore.toFixed(3)} ·{' '}
              {memory.calibrationBiasPct >= 0 ? '+' : ''}
              {memory.calibrationBiasPct.toFixed(1)}pp calibration bias
            </p>

            <div className="mt-2 grid gap-2 sm:grid-cols-2">
              <div>
                <p className="type-eyebrow text-fg-subtle">Paid</p>
                {memory.bestSectors.length === 0 ? (
                  <p className="mt-1 text-xs text-fg-faint">No sector sample yet.</p>
                ) : (
                  <ul className="mt-1 space-y-1">
                    {memory.bestSectors.map((sector) => (
                      <li key={`best-${sector.sector}`} className="flex justify-between text-xs">
                        <span className="text-fg-muted">{sector.sector}</span>
                        <span className="tabular text-mint-400">
                          {formatR(sector.averageRealizedNetR)} · {formatInteger(sector.samples)}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
              <div>
                <p className="type-eyebrow text-fg-subtle">Bled</p>
                {memory.worstSectors.length === 0 ? (
                  <p className="mt-1 text-xs text-fg-faint">No sector sample yet.</p>
                ) : (
                  <ul className="mt-1 space-y-1">
                    {memory.worstSectors.map((sector) => (
                      <li key={`worst-${sector.sector}`} className="flex justify-between text-xs">
                        <span className="text-fg-muted">{sector.sector}</span>
                        <span className="tabular text-rose-400">
                          {formatR(sector.averageRealizedNetR)} · {formatInteger(sector.samples)}
                        </span>
                      </li>
                    ))}
                  </ul>
                )}
              </div>
            </div>

            {memory.recentStopOuts.length > 0 ? (
              <div className="mt-2">
                <p className="type-eyebrow text-fg-subtle">Latest stop-outs</p>
                <ul className="mt-1 space-y-1">
                  {memory.recentStopOuts.map((stopOut) => (
                    <li
                      key={`${stopOut.ticker}-${stopOut.runDate}`}
                      className="flex flex-wrap justify-between gap-2 text-xs"
                    >
                      <span className="font-mono font-semibold tracking-wide text-fg">
                        {stopOut.ticker}
                      </span>
                      <span className="tabular text-fg-subtle">
                        {formatDate(stopOut.runDate)} · stop {stopOut.stopLoss.toFixed(2)} ·{' '}
                        {formatInteger(stopOut.barsHeld)} bars
                      </span>
                      <span className="tabular text-rose-400">{formatR(stopOut.realizedNetR)}</span>
                    </li>
                  ))}
                </ul>
              </div>
            ) : null}
          </>
        )}
      </section>

      {models.length > 1 ? (
        <section className="mt-3 rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <h3 className="type-eyebrow text-fg-subtle">All four books</h3>
          <ul className="mt-2 space-y-1 md:hidden">
            {models.map((model) => (
              <li
                key={String(model.horizon)}
                className={cn(
                  'rounded-lg border border-line/60 bg-ink-900/60 px-2.5 py-1.5',
                  model.horizon === horizon && 'border-line bg-ink-800/60',
                )}
              >
                <div className="flex items-center justify-between gap-2">
                  <span className="text-xs font-semibold text-fg">{model.horizon}</span>
                  <span
                    className={cn(
                      'rounded-full border px-2 py-0.5 type-badge',
                      statusTone(model.status),
                    )}
                  >
                    {model.status.replace('_', ' ').toLowerCase()}
                  </span>
                </div>
                <p className="tabular mt-1 flex flex-wrap gap-x-3 gap-y-1 text-2xs text-fg-subtle">
                  <span>
                    Brier{' '}
                    {model.trainSampleCount + model.validationSampleCount > 0
                      ? model.trainedBrierScore.toFixed(3)
                      : '—'}
                  </span>
                  <span>
                    base{' '}
                    {model.trainSampleCount + model.validationSampleCount > 0
                      ? model.baselineBrierScore.toFixed(3)
                      : '—'}
                  </span>
                  <span>
                    Kelly{' '}
                    {model.optimalKellyFractionMultiplier > 0
                      ? `${model.optimalKellyFractionMultiplier.toFixed(2)}x`
                      : '—'}
                  </span>
                </p>
              </li>
            ))}
          </ul>
          <table className="mt-2 hidden w-full border-collapse text-left md:table">
            <thead>
              <tr className="type-eyebrow text-fg-subtle">
                <th className="py-1.5 font-medium">Book</th>
                <th className="py-1.5 font-medium">Status</th>
                <th className="py-1.5 text-right font-medium">Held-out Brier</th>
                <th className="py-1.5 text-right font-medium">Baseline</th>
                <th className="py-1.5 text-right font-medium">Kelly</th>
              </tr>
            </thead>
            <tbody>
              {models.map((model) => (
                <tr
                  key={String(model.horizon)}
                  className={cn('border-t border-line/50', model.horizon === horizon && 'bg-ink-900/60')}
                >
                  <td className="py-1.5 text-xs font-semibold text-fg">{model.horizon}</td>
                  <td className="py-1.5">
                    <span
                      className={cn(
                        'rounded-full border px-2 py-0.5 type-badge',
                        statusTone(model.status),
                      )}
                    >
                      {model.status.replace('_', ' ').toLowerCase()}
                    </span>
                  </td>
                  <td className="tabular py-1.5 text-right text-xs text-fg-muted">
                    {model.trainSampleCount + model.validationSampleCount > 0
                      ? model.trainedBrierScore.toFixed(3)
                      : '—'}
                  </td>
                  <td className="tabular py-1.5 text-right text-xs text-fg-subtle">
                    {model.trainSampleCount + model.validationSampleCount > 0
                      ? model.baselineBrierScore.toFixed(3)
                      : '—'}
                  </td>
                  <td className="tabular py-1.5 text-right text-xs text-fg-muted">
                    {model.optimalKellyFractionMultiplier > 0
                      ? `${model.optimalKellyFractionMultiplier.toFixed(2)}x`
                      : '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <p className="mt-2 type-caption text-fg-faint">
            A weekly refit runs after the first trading session of each week, so the next session prices
            with weights trained only on outcomes that had already resolved.
          </p>
        </section>
      ) : null}
    </div>
  );
}

function describeTraining(summary: TrainedModelSummary): string {
  if (summary.status === 'LEARNED') {
    return (
      `Trained the ${summary.horizon} book: weights promoted. Held-out Brier ${summary.trainedBrierScore.toFixed(3)} ` +
      `beats the ${summary.baselineBrierScore.toFixed(3)} neutral prior over ${formatInteger(summary.validationSampleCount)} ` +
      `trades, so live odds now carry a bounded learned tilt from ${formatInteger(summary.trainSampleCount)} training trades.`
    );
  }

  return (
    `Fitted the ${summary.horizon} book but did not promote it: held-out Brier ${summary.trainedBrierScore.toFixed(3)} ` +
    `did not clear the ${summary.baselineBrierScore.toFixed(3)} neutral prior over ${formatInteger(summary.validationSampleCount)} ` +
    'trades. The structural prior still prices every pick — that is the honest result, not a failure to hide.'
  );
}

