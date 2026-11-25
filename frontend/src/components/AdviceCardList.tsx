import { useState } from 'react';
import type { Horizon, SignalItem, WaveTradePlan } from '../types/signals';
import { cn } from '../lib/cn';
import { formatCompactPeso, formatInteger, formatPrice, formatRatio } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import {
  computePlanPercentages,
  describeConviction,
  describeEntryTiming,
  describeOrderGuide,
  describePlainVerdict,
  describeWinChance,
  plainVerdictBadgeClass,
  type PlainOrderGuide,
} from '../lib/plainAdvice';
import { companyLabel, nicknameFor } from '../lib/pseNicknames';
import { preloadPortfolioDialog, preloadTickerDetailDialog } from '../lib/preloaders';
import { computeRiskSizing, formatProbability, formatR } from '../lib/riskSizing';
import { DriftBadge } from './DriftBadge';
import { Button } from './ui/Button';
import { InfoTip } from './ui/InfoTip';
import { TechnicalDisclosure } from './ui/TechnicalDisclosure';

interface AdviceCardListProps {
  items: SignalItem[];
  horizon: Horizon;
  /** Opens the full detail dialog for a ticker. */
  onSelect: (ticker: string) => void;
  /** Logs the pick's server ticket in the paper blotter. */
  onTakeTrade: (item: SignalItem) => void;
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
      tone: 'text-slate-100',
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
    <ol className="grid grid-cols-1 gap-2 sm:grid-cols-3">
      {steps.map((step) => (
        <li key={step.label} className="rounded-lg border border-line/70 bg-ink-900/70 px-2.5 py-2">
          <p className="flex items-center gap-1 text-[9px] uppercase tracking-[0.14em] text-slate-500 simple-label">
            {step.label}
            <InfoTip label={step.hint} term={step.label.replace(/^\d+ · /, '')} />
          </p>
          <p className={cn('tabular mt-0.5 text-sm font-semibold', step.tone)}>{formatPrice(step.price)}</p>
          <p className="tabular text-[10px] text-slate-500">{step.note}</p>
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
      tone: 'text-slate-100',
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
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
        <span
          className={cn(
            'rounded-full border px-2 py-0.5 text-[9px] font-semibold uppercase tracking-[0.12em]',
            timing.badgeClass,
          )}
        >
          {timing.label}
        </span>
        <span className="text-[10px] leading-relaxed text-slate-500">{timing.detail}</span>
      </div>

      <ol className="grid grid-cols-2 gap-2 xl:grid-cols-4">
        {steps.map((step) => (
          <li key={step.label} className="rounded-lg border border-line/70 bg-ink-900/70 px-2.5 py-2">
            <p className="text-[9px] uppercase tracking-[0.14em] text-slate-500 simple-label">{step.label}</p>
            <p className={cn('tabular mt-0.5 text-sm font-semibold', step.tone)}>{formatPrice(step.price)}</p>
            <p className="mt-0.5 text-[10px] leading-relaxed text-slate-500">{step.note}</p>
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

/** The broker taps stay one tap away instead of lengthening every card. */
function BrokerChecklistToggle({ item, guide }: { item: SignalItem; guide: PlainOrderGuide }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="mt-2">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="text-[10px] font-semibold text-slate-400 underline decoration-line/80 underline-offset-2 transition hover:text-slate-200"
      >
        {open ? 'Hide the broker steps' : 'Show the exact taps in your broker app'}
      </button>
      {open ? <BrokerChecklist item={item} guide={guide} /> : null}
    </div>
  );
}

/** Friendly confidence meter; `pct` is only used for the bar width. */
function Meter({
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
    <div className="rounded-lg border border-line/70 bg-ink-900/70 px-3 py-2">
      <div className="flex items-baseline justify-between gap-2">
        <p className="text-[9px] uppercase tracking-[0.14em] text-slate-500">{label}</p>
        <p className="tabular text-[11px] font-semibold text-slate-200">{value}</p>
      </div>
      <div className="mt-1.5 h-1.5 overflow-hidden rounded-full bg-ink-700">
        <div className={cn('h-full rounded-full', barClass)} style={{ width }} />
      </div>
      <p className="mt-1 text-[10px] leading-relaxed text-slate-500">{detail}</p>
    </div>
  );
}

/** Peso-sized buying instructions, straight from the server order ticket. */
export function OrderBox({ guide, item }: { guide: PlainOrderGuide; item?: SignalItem }) {
  if (!guide.actionable) {
    return (
      <div className="rounded-lg border border-line/70 bg-ink-900/70 px-3 py-2">
        <p className="text-[11px] font-semibold text-slate-300">Skip this one for now</p>
        <p className="mt-1 text-[11px] leading-relaxed text-slate-400">{guide.orderTip}</p>
        {guide.warnings.length > 0 ? (
          <ul className="mt-1.5 space-y-0.5">
            {guide.warnings.map((warning) => (
              <li key={warning} className="text-[10px] leading-relaxed text-amber-300/90">
                {warning}
              </li>
            ))}
          </ul>
        ) : null}
      </div>
    );
  }

  return (
    <div className="rounded-lg border border-mint-400/25 bg-mint-400/5 px-3 py-2">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <p className="text-[11px] font-semibold text-slate-100">
          Buy {formatInteger(guide.shares)} share{guide.shares === 1 ? '' : 's'}
          <span className="font-normal text-slate-500">
            {' '}
            ({guide.lots} board lot{guide.lots === 1 ? '' : 's'} of {guide.boardLot})
          </span>
        </p>
        <p className="tabular text-[11px] text-slate-300">
          about {formatCompactPeso(guide.cashNeededPhp)} all-in
        </p>
      </div>
      <p className="mt-1 text-[11px] leading-relaxed text-slate-400">
        If it reaches the target you keep about{' '}
        <span className="font-semibold text-mint-400">{formatCompactPeso(guide.netGainAtTargetPhp)}</span> after broker
        fees. If it hits the cut-loss you lose about{' '}
        <span className="font-semibold text-rose-400">{formatCompactPeso(guide.netLossAtStopPhp)}</span>.
      </p>
      <p className="mt-1 text-[11px] leading-relaxed text-slate-500">{guide.orderTip}</p>
      {guide.warnings.length > 0 ? (
        <ul className="mt-1.5 space-y-0.5">
          {guide.warnings.map((warning) => (
            <li key={warning} className="text-[10px] leading-relaxed text-amber-300/90">
              {warning}
            </li>
          ))}
        </ul>
      ) : null}
      {guide.serverSized ? null : (
        <p className="mt-1 text-[10px] text-slate-600">
          Estimated size — this older session has no server order ticket.
        </p>
      )}
      {item ? <BrokerChecklistToggle item={item} guide={guide} /> : null}
    </div>
  );
}

/**
 * Beginner bridge: the exact taps to make inside any online broker once the card looks right.
 * Every number comes from the server order ticket or the server plan — nothing is recalculated.
 */
export function BrokerChecklist({ item, guide }: { item: SignalItem; guide: PlainOrderGuide }) {
  const lots = `${guide.lots} board lot${guide.lots === 1 ? '' : 's'} of ${formatInteger(guide.boardLot)}`;

  return (
    <div className="mt-2 rounded-lg border border-line/70 bg-ink-900/60 px-3 py-2">
      <p className="text-[9px] font-semibold uppercase tracking-[0.14em] text-slate-500 simple-label">
        In your broker app
      </p>
      <ol className="mt-1 space-y-1 text-[11px] leading-relaxed text-slate-400">
        <li>
          1. Search for <span className="font-mono font-semibold text-slate-200">{item.ticker}</span>.
        </li>
        <li>
          2. Tap <span className="text-slate-200">Buy</span>, then choose a{' '}
          <span className="text-slate-200">Limit order</span> (not market).
        </li>
        <li>
          3. Quantity: <span className="tabular text-slate-200">{formatInteger(guide.shares)} shares</span> ({lots}).
        </li>
        <li>
          4. Limit price: <span className="tabular text-slate-200">{formatPrice(item.entryPrice)}</span> — never pay
          more.
        </li>
        <li>
          5. Cash needed: about {formatCompactPeso(guide.cashNeededPhp)}, broker fees already included.
        </li>
        <li>
          6. Set a reminder to sell at{' '}
          <span className="tabular text-mint-400">{formatPrice(item.targetPrice)}</span> or cut at{' '}
          <span className="tabular text-rose-400">{formatPrice(item.stopLoss)}</span>.
        </li>
      </ol>
    </div>
  );
}

function TechRow({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex items-baseline justify-between gap-2 border-b border-line/40 py-1 last:border-b-0">
      <dt className="text-[10px] uppercase tracking-[0.12em] text-slate-500">{label}</dt>
      <dd className="tabular text-right text-[11px] text-slate-300">{value}</dd>
    </div>
  );
}

/** Verbatim desk fields — the receipt behind every friendly sentence on the card. */
function TechnicalRows({ item, guide }: { item: SignalItem; guide: PlainOrderGuide }) {
  const sizing = computeRiskSizing(item);
  const metrics = item.metrics;

  return (
    <div className="grid gap-4 sm:grid-cols-2">
      <div>
        <p className="text-[9px] font-semibold uppercase tracking-[0.16em] text-slate-500">Plan &amp; size</p>
        <dl className="mt-1">
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

      <div>
        <p className="text-[9px] font-semibold uppercase tracking-[0.16em] text-slate-500">Raw indicators</p>
        <dl className="mt-1">
          <TechRow label="Close" value={formatPrice(metrics?.closePrice)} />
          <TechRow label="RSI14" value={metrics ? metrics.rsi14.toFixed(1) : '—'} />
          <TechRow label="EMA20" value={formatPrice(metrics?.ema20)} />
          <TechRow label="SMA50" value={formatPrice(metrics?.sma50)} />
          <TechRow label="SMA200" value={formatPrice(metrics?.sma200)} />
          <TechRow label="ATR14" value={formatPrice(metrics?.atr14)} />
          <TechRow label="Volume vs 20D" value={metrics ? `${metrics.volumeRatioVs20d.toFixed(2)}×` : '—'} />
          <TechRow label="20D turnover" value={formatCompactPeso(metrics?.avgTurnover20dPhp)} />
          <TechRow
            label="RS63 vs PSEi"
            value={metrics?.relativeStrength63d != null ? `${metrics.relativeStrength63d.toFixed(1)} pts` : '—'}
          />
          <TechRow
            label="CMF20"
            value={metrics?.chaikinMoneyFlow20d != null ? metrics.chaikinMoneyFlow20d.toFixed(1) : '—'}
          />
          <TechRow label="Weinstein stage" value={metrics?.weinsteinStage ?? '—'} />
          <TechRow label="Net foreign today" value={formatCompactPeso(metrics?.netForeignTodayPhp)} />
          <TechRow label="Trap flags" value={item.trapFlags?.length ? item.trapFlags.join(', ') : '—'} />
        </dl>

        <p className="mt-3 text-[9px] font-semibold uppercase tracking-[0.16em] text-slate-500">
          Why it moved
        </p>
        <p className="mt-1 text-[11px] leading-relaxed text-slate-400">{item.deviationReason}</p>

        {item.disclosures.length > 0 ? (
          <>
            <p className="mt-3 text-[9px] font-semibold uppercase tracking-[0.16em] text-slate-500">
              Filings on file
            </p>
            <ul className="mt-1 space-y-1">
              {item.disclosures.slice(0, 4).map((disclosure) => (
                <li key={`${disclosure.date}-${disclosure.headline}`} className="text-[10px] leading-relaxed text-slate-400">
                  <span className="tabular text-slate-500">{disclosure.date}</span> · {disclosure.category} ·{' '}
                  {disclosure.headline}
                </li>
              ))}
            </ul>
          </>
        ) : null}
      </div>
    </div>
  );
}

interface AdviceCardProps {
  item: SignalItem;
  horizon: Horizon;
  onSelect: (ticker: string) => void;
  onTakeTrade: (item: SignalItem) => void;
}

/** One plain-English trade plan: what to buy, when to sell, when to give up. */
function AdviceCard({ item, horizon, onSelect, onTakeTrade }: AdviceCardProps) {
  const verdict = describePlainVerdict(item.edgeVerdict);
  const guide = describeOrderGuide(item);
  const beatsBreakEven = item.winProbability >= item.breakEvenWinRate;

  return (
    <article className="flex flex-col gap-3 rounded-xl border border-line/70 bg-ink-900/60 p-3.5">
      <header className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <div className="flex flex-wrap items-center gap-2">
            <span className="tabular text-[10px] font-semibold text-slate-500">#{item.rank}</span>
            <button
              type="button"
              onClick={() => onSelect(item.ticker)}
              onPointerEnter={() => preloadTickerDetailDialog(item.ticker, horizon)}
              onFocus={() => preloadTickerDetailDialog(item.ticker, horizon)}
              className="text-left text-base font-semibold leading-tight text-white transition hover:text-mint-400"
            >
              {companyLabel(item.ticker, item.companyName)}
            </button>
            <span className="rounded border border-line/70 bg-ink-850 px-1.5 py-0.5 font-mono text-[10px] tracking-wider text-slate-400">
              {item.ticker}
            </span>
            <span
              className={cn(
                'rounded-full border px-2 py-0.5 text-[10px] font-semibold uppercase tracking-[0.12em]',
                plainVerdictBadgeClass(item.edgeVerdict),
              )}
            >
              {verdict.label}
            </span>
            <DriftBadge status={item.driftStatus} previousRank={item.previousRank} friendly />
          </div>
          <p className="mt-0.5 truncate text-[11px] text-slate-500">
            {nicknameFor(item.ticker) ? `${item.companyName} · ${item.sector}` : item.sector}
          </p>
        </div>
        <div className="text-right">
          <p className="tabular text-lg font-semibold text-slate-100">{formatPrice(item.entryPrice)}</p>
          <p className="flex items-center justify-end gap-1 text-[10px] text-slate-500">
            buy price
            <InfoTip
              term="buy price"
              label="The price the desk would pay today. It is the ceiling for this plan — anything higher and the reward no longer covers the risk."
            />
          </p>
        </div>
      </header>

      <p className="text-xs leading-relaxed text-slate-300">{item.thesis}</p>

      <WavePlanSteps item={item} />
      <p className="text-[10px] uppercase tracking-[0.14em] text-slate-500">
        Plan to hold for about{' '}
        {item.wavePlan ? `up to ${item.wavePlan.maxHoldTradingDays} session${item.wavePlan.maxHoldTradingDays === 1 ? '' : 's'}` : horizonMeta[horizon].holding}
      </p>

      <OrderBox guide={guide} item={item} />

      <div className="grid gap-2 sm:grid-cols-2">
        <Meter
          label="Confidence"
          pct={item.convictionScore}
          value={`${Math.round(item.convictionScore)} / 100`}
          detail={describeConviction(item.convictionScore)}
          barClass="bg-linear-to-r from-mint-500 to-mint-400"
        />
        <Meter
          label="Chance it reaches the target"
          pct={item.winProbability * 100}
          value={formatProbability(item.winProbability)}
          detail={describeWinChance(item.winProbability, item.breakEvenWinRate)}
          barClass={beatsBreakEven ? 'bg-sky-400' : 'bg-rose-400'}
        />
      </div>

      <div className="rounded-lg border border-amber-400/30 bg-amber-400/5 px-3 py-2">
        <p className="text-[9px] font-semibold uppercase tracking-[0.14em] text-amber-400">
          What would prove this wrong
        </p>
        <p className="mt-1 text-[11px] leading-relaxed text-slate-300">{item.bearCase}</p>
      </div>

      <div className="flex flex-wrap items-center gap-2">
        <Button
          variant="primary"
          disabled={!guide.actionable}
          onClick={() => onTakeTrade(item)}
          onPointerEnter={preloadPortfolioDialog}
          onFocus={preloadPortfolioDialog}
        >
          Practise This Trade
        </Button>
        <Button
          variant="secondary"
          onClick={() => onSelect(item.ticker)}
          onPointerEnter={() => preloadTickerDetailDialog(item.ticker, horizon)}
          onFocus={() => preloadTickerDetailDialog(item.ticker, horizon)}
        >
          Full Details
        </Button>
        <span className="text-[10px] text-slate-500">
          {guide.actionable
            ? 'Adds it to your practice account — no real order is sent.'
            : 'No board lot fits the paper account at this price.'}
        </span>
      </div>

      <TechnicalDisclosure title="Show the math behind this pick">
        <TechnicalRows item={item} guide={guide} />
      </TechnicalDisclosure>
    </article>
  );
}

/** The honest "not worth your money today" bucket, collapsed by default. */
function WatchList({ items, onSelect }: { items: SignalItem[]; onSelect: (ticker: string) => void }) {
  const [open, setOpen] = useState(false);

  return (
    <div className="mt-4 rounded-xl border border-line/70 bg-ink-850/50">
      <button
        type="button"
        aria-expanded={open}
        onClick={() => setOpen((current) => !current)}
        className="flex w-full flex-wrap items-center justify-between gap-2 px-3 py-2 text-left transition hover:bg-ink-800/60"
      >
        <span className="flex flex-wrap items-center gap-2">
          <span aria-hidden className="text-[10px] text-slate-500">
            {open ? '▾' : '▸'}
          </span>
          <span className="text-[11px] font-semibold text-slate-300">
            Not yet — these {items.length} can wait
          </span>
        </span>
        <span className="text-[10px] text-slate-500">the reward after fees is too thin</span>
      </button>

      {open ? (
        <ul className="grid gap-2 border-t border-line/60 px-3 py-3 sm:grid-cols-2 xl:grid-cols-3">
          {items.map((item) => (
            <li key={item.ticker} className="rounded-lg border border-line/70 bg-ink-900/70 px-2.5 py-2">
              <div className="flex items-baseline justify-between gap-2">
                <button
                  type="button"
                  onClick={() => onSelect(item.ticker)}
                  className="text-left text-xs font-semibold text-slate-200 transition hover:text-mint-400"
                >
                  {companyLabel(item.ticker, item.companyName)}
                </button>
                <span className="tabular text-[10px] text-slate-500">{formatPrice(item.entryPrice)}</span>
              </div>
              <p className="truncate font-mono text-[10px] text-slate-500">
                {item.ticker} · {item.companyName}
              </p>
              <p className="mt-1 text-[10px] leading-relaxed text-slate-400">
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
 * Simple-mode home: the buyable names as plain trade cards, with every raw desk figure one
 * click away inside a per-card drawer. Nothing here is recomputed — the card only renames
 * server fields and reformats the server order ticket.
 */
export function AdviceCardList({ items, horizon, onSelect, onTakeTrade }: AdviceCardListProps) {
  const meta = horizonMeta[horizon];
  const buyable = items.filter((item) => item.edgeVerdict !== 'WATCH_ONLY');
  const watchOnly = items.filter((item) => item.edgeVerdict === 'WATCH_ONLY');
  const [showAll, setShowAll] = useState(false);
  // Simple mode leads with the three names that matter most; the rest stay one tap away so
  // a beginner is not asked to read ten plans to make one decision.
  const visible = showAll ? buyable : buyable.slice(0, 3);
  const hiddenCount = Math.max(0, buyable.length - visible.length);

  return (
    <section className="panel p-4 sm:p-5">
      <header className="flex flex-wrap items-end justify-between gap-2">
        <div>
          <h2 className="text-sm font-semibold text-slate-100">{meta.friendlyTitle}</h2>
          <p className="mt-1 max-w-3xl text-[11px] leading-relaxed text-slate-500">{meta.friendlySummary}</p>
        </div>
        <span className="tabular rounded-full border border-line/80 bg-ink-850 px-2.5 py-1 text-[10px] font-semibold tracking-[0.14em] text-slate-400">
          {buyable.length} TO BUY · {watchOnly.length} TO WAIT
        </span>
      </header>

      <p className="mt-2 text-[10px] uppercase tracking-[0.14em] text-slate-600 simple-label">
        Ranked for {meta.friendlyTitle.toLowerCase()} · fees and board lots already priced in
      </p>

      {buyable.length === 0 ? (
        <div className="mt-4 rounded-xl border border-dashed border-line/80 bg-ink-950/40 px-4 py-6 text-center">
          <p className="text-xs font-semibold text-slate-300">Nothing is worth buying today</p>
          <p className="mx-auto mt-1 max-w-xl text-xs leading-relaxed text-slate-500">
            That is a real answer, not a glitch — the desk would rather keep your money in cash than take a trade
            that does not pay its fees. Come back after the next session.
          </p>
        </div>
      ) : (
        <div
          className={cn(
            'mt-3 grid gap-3',
            visible.length >= 3
              ? 'lg:grid-cols-2 min-[1760px]:grid-cols-3'
              : 'xl:grid-cols-2',
          )}
        >
          {visible.map((item) => (
            <AdviceCard key={item.ticker} item={item} horizon={horizon} onSelect={onSelect} onTakeTrade={onTakeTrade} />
          ))}
        </div>
      )}

      {buyable.length > 3 ? (
        <button
          type="button"
          aria-expanded={showAll}
          onClick={() => setShowAll((current) => !current)}
          className="mt-3 w-full rounded-xl border border-line/70 bg-ink-850/60 px-3 py-2 text-[11px] font-semibold text-slate-300 transition hover:bg-ink-800/60"
        >
          {showAll
            ? 'Show just the top three'
            : `Show ${hiddenCount} more buyable pick${hiddenCount === 1 ? '' : 's'}`}
        </button>
      ) : null}

      {watchOnly.length > 0 ? <WatchList items={watchOnly} onSelect={onSelect} /> : null}
    </section>
  );
}
