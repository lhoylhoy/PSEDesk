import { useState } from 'react';
import type { Horizon, SignalItem, WaveTradePlan } from '../types/signals';
import { cn } from '../lib/cn';
import { formatCompactPeso, formatInteger, formatPrice, formatRatio, formatWeekday } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import {
  computePlanPercentages,
  describeEntryTiming,
  describeNextStep,
  describePlainVerdict,
  describeSignalRow,
  plainVerdictBadgeClass,
  type PlainOrderGuide,
} from '../lib/plainAdvice';
import { companyLabel } from '../lib/pseNicknames';
import { preloadTickerDetailDialog } from '../lib/preloaders';
import { computeRiskSizing, formatProbability, formatR } from '../lib/riskSizing';
import { DriftBadge } from './DriftBadge';
import { InfoTip } from './ui/InfoTip';

interface AdviceCardListProps {
  items: SignalItem[];
  horizon: Horizon;
  /** The stored session being read; the heading names it instead of saying "today". */
  runDate?: string | null;
  /** True while a session other than the newest one is on screen. */
  historical?: boolean;
  /** Opens the ticker's detail modal — the same sheet every row and the Pro table open. */
  onSelect: (ticker: string) => void;
}

/** The three prices a reader actually needs, in the order they use them. */
export function PlanSteps({
  entryPrice,
  targetPrice,
  stopLoss,
}: {
  entryPrice: number;
  targetPrice: number;
  stopLoss: number;
}) {
  const { upsidePct, downsidePct } = computePlanPercentages(entryPrice, targetPrice, stopLoss);

  const steps = [
    {
      label: '1 · Buy at',
      hint: 'The most you should pay. Buying higher than this makes the plan worse than the desk priced.',
      price: entryPrice,
      note: 'the most you should pay',
      tone: 'text-fg',
    },
    {
      label: '2 · Sell at',
      hint: 'The price where the desk takes the profit. Selling earlier leaves money on the table; later risks giving the gain back.',
      price: targetPrice,
      note: upsidePct === null ? 'target' : `about ${upsidePct.toFixed(1)}% higher`,
      tone: 'text-mint-400',
    },
    {
      label: '3 · Cut loss at',
      hint: 'If the price falls here, sell. It keeps one bad trade from becoming a big loss.',
      price: stopLoss,
      note: downsidePct === null ? 'stop' : `about ${Math.abs(downsidePct).toFixed(1)}% lower`,
      tone: 'text-rose-400',
    },
  ];

  return (
    <ol className="grid grid-cols-1 gap-2.5 @sm:grid-cols-3">
      {steps.map((step) => (
        <li key={step.label} className="rounded-lg border border-line/70 bg-ink-900/70 px-3.5 py-3">
          <p className="flex items-center gap-1 type-eyebrow text-fg-subtle simple-label">
            {step.label}
            <InfoTip label={step.hint} term={step.label.replace(/^\d+ · /, '')} />
          </p>
          <p className={cn('type-figure mt-1 text-base', step.tone)}>{formatPrice(step.price)}</p>
          <p className="tabular type-caption mt-0.5 text-fg-subtle simple-note">{step.note}</p>
        </li>
      ))}
    </ol>
  );
}

/**
 * The four-step ride-the-wave roadmap, straight from the server's wave plan: buy inside a
 * band, know where the idea is wrong, lock break-even at +1R and trail at +1.5R. The
 * browser only formats the tick-valid prices it was handed.
 */
export function WavePlanGrid({ plan }: { plan: WaveTradePlan }) {
  const timing = describeEntryTiming(plan.entryStatus, plan.idealBuyPrice, plan.maxChasePrice);
  const steps = [
    {
      label: '1 · Buy between',
      price: plan.idealBuyPrice,
      note:
        plan.maxChasePrice > plan.idealBuyPrice
          ? `never pay above ${formatPrice(plan.maxChasePrice)}`
          : 'do not pay a higher price',
      tone: 'text-fg',
    },
    {
      label: '2 · Wrong below',
      price: plan.cutLossPrice,
      note: 'sell here — the idea is broken',
      tone: 'text-rose-400',
    },
    {
      label: '3 · Lock break-even',
      price: plan.breakEvenStopPrice,
      note: `raise the stop once it touches ${formatPrice(plan.lockBreakEvenTriggerPrice)}`,
      tone: 'text-amber-400',
    },
    {
      label: '4 · Ride to target',
      price: plan.targetPrice,
      note:
        plan.trailStopPrice != null
          ? `trail under ${formatPrice(plan.trailStopPrice)} after ${formatPrice(plan.trailWaveTriggerPrice)}`
          : `take profit at target · up to ${plan.maxHoldTradingDays} session${plan.maxHoldTradingDays === 1 ? '' : 's'}`,
      tone: 'text-mint-400',
    },
  ];

  return (
    <div className="space-y-3">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <span
          className={cn(
            'rounded-full border px-2 py-0.5 type-badge',
            timing.badgeClass,
          )}
        >
          {timing.label}
        </span>
        <span className="type-caption measure text-fg-subtle simple-note">{timing.detail}</span>
      </div>

      <ol className="grid grid-cols-2 gap-2.5 @xl:grid-cols-4">
        {steps.map((step) => (
          <li key={step.label} className="rounded-lg border border-line/70 bg-ink-900/70 px-3.5 py-3">
            <p className="type-eyebrow text-fg-subtle simple-label">{step.label}</p>
            <p className={cn('type-figure mt-1 text-base', step.tone)}>{formatPrice(step.price)}</p>
            <p className="type-caption mt-1 text-fg-subtle simple-note">{step.note}</p>
          </li>
        ))}
      </ol>
    </div>
  );
}

/** Card-level wrapper: use the server wave plan when present, else the legacy three steps. */
export function WavePlanSteps({ item }: { item: SignalItem }) {
  if (!item.wavePlan) {
    return <PlanSteps entryPrice={item.entryPrice} targetPrice={item.targetPrice} stopLoss={item.stopLoss} />;
  }

  return <WavePlanGrid plan={item.wavePlan} />;
}

/** Friendly confidence meter; `pct` is only used for the bar width. */
export function Meter({
  label,
  pct,
  value,
  detail,
  barClass,
}: {
  label: string;
  pct: number;
  value: string;
  detail: string;
  barClass: string;
}) {
  const width = `${Math.max(3, Math.min(100, pct))}%`;

  return (
    <div className="rounded-lg border border-line/70 bg-ink-900/70 px-3.5 py-3">
      <div className="flex items-baseline justify-between gap-2">
        <p className="type-eyebrow text-fg-subtle simple-label">{label}</p>
        <p className="type-figure text-base text-fg">{value}</p>
      </div>
      <div className="mt-2 h-2 overflow-hidden rounded-full bg-ink-700">
        <div className={cn('h-full rounded-full', barClass)} style={{ width }} />
      </div>
      <p className="type-caption measure mt-1.5 text-fg-subtle simple-note">{detail}</p>
    </div>
  );
}

/**
 * The server-sized ticket, reported rather than prescribed.
 *
 * This used to spell out the taps to make inside a broker app ("Tap Buy, then choose Limit order").
 * Execution directions are the part of a retail terminal closest to a dealing service, so the panel
 * now reports the ticket the sizing engine produced and leaves the order — and the decision — to
 * the reader and their broker.
 */
export function OrderBox({ guide, item }: { guide: PlainOrderGuide; item?: SignalItem }) {
  if (!guide.actionable) {
    return (
      <div className="rounded-lg border border-line/70 bg-ink-900/70 px-3.5 py-3">
        <p className="type-body font-semibold text-fg-muted simple-copy">Watch only — no ticket</p>
        <p className="type-body measure mt-1 text-fg-muted simple-copy">{guide.orderTip}</p>
        {guide.warnings.length > 0 ? (
          <ul className="mt-2 space-y-1">
            {guide.warnings.map((warning) => (
              <li key={warning} className="type-caption measure text-amber-300/90 simple-note">
                {warning}
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    );
  }

  return (
    <div className="rounded-lg border border-mint-400/25 bg-mint-400/5 px-3.5 py-3">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <p className="type-body font-semibold text-fg simple-copy">
          Ticket: {formatInteger(guide.shares)} share{guide.shares === 1 ? '' : 's'}
          <span className="font-normal text-fg-subtle">
            {' '}
            ({guide.lots} board lot{guide.lots === 1 ? '' : 's'} of {guide.boardLot})
          </span>
        </p>
        <p className="tabular type-caption text-fg-muted simple-note">
          about {formatCompactPeso(guide.cashNeededPhp)} all-in
        </p>
      </div>
      <p className="type-body measure mt-1.5 text-fg-muted simple-copy">
        If the price reaches the target the ticket keeps about{' '}
        <span className="font-semibold text-mint-400">{formatCompactPeso(guide.netGainAtTargetPhp)}</span> after broker
        fees; at the cut-loss it gives up about{' '}
        <span className="font-semibold text-rose-400">{formatCompactPeso(guide.netLossAtStopPhp)}</span>.
      </p>
      <p className="type-body measure mt-1.5 text-fg-subtle simple-copy">{guide.orderTip}</p>
      {guide.warnings.length > 0 ? (
        <ul className="mt-2 space-y-1">
          {guide.warnings.map((warning) => (
            <li key={warning} className="type-caption measure text-amber-300/90 simple-note">
              {warning}
            </li>
          ))}
        </ul>
      ) : null}
      {guide.serverSized ? null : (
        <p className="type-caption mt-1.5 text-fg-faint simple-note">
          Estimated size — this older session has no server order ticket.
        </p>
      )}
      {item ? (
        <p className="type-caption measure mt-1.5 text-fg-faint simple-note">
          Placing an order is your decision and your broker’s execution; nothing here is routed or sent.
        </p>
      ) : null}
    </div>
  );
}

function TechRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-3 border-b border-line/40 py-1.5 last:border-b-0">
      {/* A field label, not an eyebrow: sentence case at the caption size reads faster
          than an uppercase ribbon, and it keeps the drawer's hierarchy below its title. */}
      <dt className="type-caption text-fg-subtle">{label}</dt>
      <dd className="tabular type-caption text-right font-medium text-fg">{value}</dd>
    </div>
  );
}

/**
 * The maths behind one ticket: how the pick scored, and what the ticket costs to carry.
 * It deliberately stops at the sizing maths — the raw indicators, the deviation reason and
 * the filings each own a drawer in the detail sheet, and one dialog should never print the
 * same figure twice.
 *
 * Every value is a verbatim desk field, the receipt behind the friendly sentence on the row.
 */
export function MathRows({ item, guide }: { item: SignalItem; guide: PlainOrderGuide }) {
  const sizing = computeRiskSizing(item);

  return (
    <div className="grid gap-5 @lg:grid-cols-2">
      <div>
        <p className="type-eyebrow text-fg-subtle">The pick</p>
        <dl className="mt-1.5">
          <TechRow label="Verdict code" value={item.edgeVerdict} />
          <TechRow
            label="Drift"
            value={item.previousRank != null ? `${item.driftStatus} · was #${item.previousRank}` : item.driftStatus}
          />
          <TechRow label="Conviction" value={item.convictionScore.toFixed(1)} />
          <TechRow label="Gross R:R" value={formatRatio(item.riskRewardRatio)} />
          <TechRow label="Net R:R (fees)" value={formatRatio(item.netRiskRewardRatio)} />
          <TechRow label="P(win)" value={formatProbability(item.winProbability, 1)} />
          <TechRow label="Break-even win rate" value={formatProbability(item.breakEvenWinRate, 1)} />
          <TechRow label="EV" value={formatR(item.expectedValueR)} />
          <TechRow label="Quarter-Kelly" value={`${(item.quarterKellyFraction * 100).toFixed(2)}%`} />
        </dl>
      </div>

      <div>
        <p className="type-eyebrow text-fg-subtle">The ticket</p>
        <dl className="mt-1.5">
          <TechRow label="Board lot" value={`${formatInteger(guide.boardLot)} shares`} />
          <TechRow label="Size" value={`${formatInteger(guide.shares)} sh · ${guide.lots} lot`} />
          <TechRow label="Risk budget" value={formatCompactPeso(sizing.budgetPhp)} />
          <TechRow label="Net risk (fees)" value={formatCompactPeso(guide.netLossAtStopPhp)} />
          <TechRow label="Net reward (fees)" value={formatCompactPeso(guide.netGainAtTargetPhp)} />
          <TechRow label="Break-even stop" value={formatPrice(guide.breakEvenStopPrice)} />
          <TechRow label="Risk governor" value={sizing.riskGovernor ?? '—'} />
          <TechRow
            label="Effective risk"
            value={sizing.effectiveRiskPct != null ? `${sizing.effectiveRiskPct.toFixed(2)}%` : '—'}
          />
          <TechRow
            label="Slippage"
            value={sizing.slippageRatePct != null ? `${sizing.slippageRatePct.toFixed(3)}%` : '—'}
          />
          <TechRow
            label="ADV participation"
            value={sizing.advParticipationPct != null ? `${sizing.advParticipationPct.toFixed(2)}%` : '—'}
          />
          <TechRow label="Execution" value={sizing.executionGuidance ?? '—'} />
          <TechRow label="Server sizing" value={guide.serverSized ? 'order ticket' : 'legacy estimate'} />
        </dl>
      </div>
    </div>
  );
}

interface SignalRowProps {
  item: SignalItem;
  horizon: Horizon;
  /** Opens the ticker's detail modal. */
  onSelect: (ticker: string) => void;
}

/**
 * One trade signal: four facts at a glance (which company, how strong the call is, what it
 * costs to buy, where the plan ends). Tapping the row opens the ticker's detail modal with
 * the whole "why" — the same sheet the Pro table's row already opens.
 *
 * Every figure is a server field — the row only renames and folds the same payload the Pro
 * table prints in full. The row is a single button, so a phone reader gets one thumb-sized
 * target, and `aria-haspopup="dialog"` tells a screen reader that a modal follows.
 */
function SignalRow({ item, horizon, onSelect }: SignalRowProps) {
  const row = describeSignalRow(item);

  return (
    <li className="border-t border-line/60 first:border-t-0">
      <button
        type="button"
        aria-haspopup="dialog"
        onClick={() => onSelect(item.ticker)}
        onPointerEnter={() => preloadTickerDetailDialog(item.ticker, horizon)}
        onFocus={() => preloadTickerDetailDialog(item.ticker, horizon)}
        className="tap flex w-full items-start gap-3 px-4 py-3.5 text-left transition hover:bg-ink-800/50 focus-visible:bg-ink-800/60"
      >
        <span className="tabular type-caption mt-0.5 w-6 shrink-0 font-semibold text-fg-subtle">
          {String(item.rank).padStart(2, '0')}
        </span>
        <span className="min-w-0 flex-1">
          <span className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span className="type-lead font-semibold text-fg">
              {companyLabel(item.ticker, item.companyName)}
            </span>
            <span className="rounded border border-line/70 bg-ink-850 px-1.5 py-0.5 font-mono text-2xs text-fg-muted">
              {item.ticker}
            </span>
            <span
              className={cn(
                'rounded-full border px-2 py-0.5 type-badge',
                plainVerdictBadgeClass(item.edgeVerdict),
              )}
            >
              {row.verdictLabel}
            </span>
            {row.entryChip ? (
              <span className="rounded-full border border-sky-400/40 bg-sky-400/10 px-2 py-0.5 type-caption font-semibold text-sky-300">
                {row.entryChip}
              </span>
            ) : null}
            <DriftBadge status={item.driftStatus} previousRank={item.previousRank} friendly />
          </span>
          <span className="tabular type-caption mt-1.5 block text-fg-muted simple-note">{row.headline}</span>
          <span className="type-caption mt-1 block text-fg-subtle simple-note">
            {row.subline}
          </span>
          {/* The row is a button that opens a sheet; say so, because "Why?" left the action
              ambiguous and the chevron alone is invisible to a screen reader. */}
          <span className="sr-only">Opens the full plan in a detail sheet.</span>
        </span>
        <span className="type-caption mt-0.5 flex shrink-0 items-center gap-1 whitespace-nowrap font-semibold text-fg-muted simple-label">
          {/* Always visible: a lone chevron was the only hint that a row was tappable. */}
          <span>View plan</span>
          <span aria-hidden>▸</span>
        </span>
      </button>
    </li>
  );
}

/** The honest "not worth your money today" bucket, collapsed by default. */
function WatchList({ items, onSelect }: { items: SignalItem[]; onSelect: (ticker: string) => void }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="mt-3 rounded-xl border border-line/70 bg-ink-850/50">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="flex w-full flex-wrap items-center justify-between gap-2 px-4 py-3 text-left transition hover:bg-ink-800/60"
      >
        <span className="flex flex-wrap items-center gap-2">
          <span aria-hidden className="text-2xs text-fg-subtle">
            {open ? '▾' : '▸'}
          </span>
          <span className="type-body font-semibold text-fg-muted simple-label">
            Not yet — these {items.length} can wait
          </span>
        </span>
        <span className="type-caption text-fg-subtle simple-note">the reward after fees is too thin</span>
      </button>

      {open ? (
        <ul className="grid gap-2.5 border-t border-line/60 px-3.5 py-3.5 @md:grid-cols-2 @5xl:grid-cols-3">
          {items.map((item) => (
            <li key={item.ticker} className="rounded-lg border border-line/70 bg-ink-900/70 px-3.5 py-3">
              <div className="flex items-baseline justify-between gap-2">
                <button
                  type="button"
                  onClick={() => onSelect(item.ticker)}
                  className="type-body text-left font-semibold text-fg transition hover:text-mint-400"
                >
                  {companyLabel(item.ticker, item.companyName)}
                </button>
                <span className="tabular type-caption text-fg-subtle simple-note">{formatPrice(item.entryPrice)}</span>
              </div>
              <p className="truncate font-mono text-2xs text-fg-subtle simple-note">
                {item.ticker} · {item.companyName}
              </p>
              <p className="type-caption measure mt-1.5 text-fg-muted simple-note">
                {describePlainVerdict(item.edgeVerdict).explanation}
              </p>
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}

/**
 * Simple mode's home: one row per trade signal, ranked. Tapping a row opens the ticker's
 * detail modal — the same sheet the Pro table's row opens — so the list itself never turns
 * back into the wall of expanded plans it replaced.
 *
 * Nothing here is recomputed — a row only renames server fields, and the modal formats the
 * server order ticket.
 */
export function AdviceCardList({ items, horizon, runDate, historical = false, onSelect }: AdviceCardListProps) {
  const meta = horizonMeta[horizon];
  const buyable = items.filter((item) => item.edgeVerdict !== 'WATCH_ONLY');
  const watchOnly = items.filter((item) => item.edgeVerdict === 'WATCH_ONLY');
  const nextStep = describeNextStep({ buyableCount: buyable.length, watchOnlyCount: watchOnly.length });

  return (
    <section className="panel @container overflow-hidden">
      <header className="flex flex-wrap items-end justify-between gap-x-4 gap-y-2 px-4 py-4 sm:px-5">
        <div>
          {/* "Today’s" is only true for the newest session; a past session is named by its date. */}
          <h2 className="type-title text-fg">
            {historical ? `Trade signals · ${formatWeekday(runDate)}` : 'Today’s trade signals'}
          </h2>
          <p className="type-caption measure mt-1.5 text-fg-subtle simple-copy">
            {meta.friendlySummary}
          </p>
        </div>
        <span className="tabular type-badge rounded-full border border-line/80 bg-ink-850 px-2.5 py-1 text-fg-muted simple-label">
          {buyable.length} to buy · {watchOnly.length} to wait
        </span>
      </header>

      <p className="type-eyebrow px-4 pb-2 text-fg-faint simple-label sm:px-5">
        {meta.friendlyTitle} · fees and board lots already priced in
      </p>

      {/* One sentence that answers "what now?" before the reader has to infer it from the rows. */}
      <p className="flex items-start gap-1.5 px-4 pb-3 type-body text-sky-300 simple-copy sm:px-5">
        <span aria-hidden className="pt-px text-fg-subtle">
          →
        </span>
        <span className="measure">{nextStep}</span>
      </p>

      {buyable.length === 0 ? (
        <div className="mx-4 mb-4 rounded-xl border border-dashed border-line/80 bg-ink-950/40 px-5 py-8 text-center sm:mx-5">
          <p className="type-lead font-semibold text-fg-muted">Nothing cleared the test today</p>
          <p className="type-body measure measure-tight mx-auto mt-2 text-fg-subtle simple-copy">
            That is a real answer, not a glitch — the model would rather hold cash than size a setup that does not
            pay its fees. The next session is screened as usual.
          </p>
        </div>
      ) : (
        <ul className="border-t border-line/60">
          {buyable.map((item) => (
            <SignalRow key={item.ticker} item={item} horizon={horizon} onSelect={onSelect} />
          ))}
        </ul>
      )}

      {watchOnly.length > 0 ? (
        <div className="px-4 pb-4 sm:px-5 sm:pb-5">
          <WatchList items={watchOnly} onSelect={onSelect} />
        </div>
      ) : null}
    </section>
  );
}
