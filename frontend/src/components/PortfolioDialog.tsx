import { useCallback, useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { portfolioApi } from '../api/signalsApi';
import type {
  ClosedTrade,
  OpenPosition,
  OpenTradeInput,
  PortfolioAccount,
  PortfolioEquityPoint,
  PortfolioSettingsUpdate,
  PortfolioSummary,
  TradeDraft,
} from '../types/portfolio';
import type { Horizon } from '../types/signals';
import { cn } from '../lib/cn';
import {
  formatDate,
  formatInteger,
  formatPercent,
  formatPrice,
  formatRatio,
  formatTimestamp,
} from '../lib/formatters';
import { formatR } from '../lib/riskSizing';
import {
  fetchQuery,
  getSnapshot,
  invalidateQueries,
  isQueryFresh,
  queryKeys,
  queryStaleTime,
  setQueryData,
} from '../lib/queryCache';
import {
  BUDGET_PRESETS_PHP,
  SAFETY_PRESETS,
  describePayoffRatio,
  describePortfolioAlert,
  describePositionAction,
  describePyramidCount,
  describeRiskPosture,
  describeTrimGuidance,
  safetyPresetForSettings,
} from '../lib/plainAdvice';
import { companyLabel } from '../lib/pseNicknames';
import type { ViewMode } from '../lib/viewMode';
import { Button } from './ui/Button';
import { Dialog, DialogClose, DialogContent, DialogDescription, DialogStickyHeader, DialogTitle } from './ui/Dialog';
import { TechnicalDisclosure } from './ui/TechnicalDisclosure';
import { Select } from './ui/Select';
import { Tabs, TabsList, TabsTrigger } from './ui/Tabs';
import { Tooltip } from './ui/Tooltip';

interface PortfolioDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  /** Pick handed over by the terminal's "Record in blotter" button; consumed once applied. */
  draft: TradeDraft | null;
  onDraftConsumed: () => void;
  /** Authoritative book already loaded by the terminal, painted on the sheet's first frame. */
  summary?: PortfolioSummary | null;
  /** Receives the fresh summary after a mutation so the shell shares one copy, not three. */
  onPortfolioMutated?: (summary: PortfolioSummary) => void;
  /** Simple mode renames the blotter and moves the guardrails into a drawer. */
  viewMode?: ViewMode;
}

type LoadStatus = 'idle' | 'loading' | 'ready' | 'error';
type TabKey = 'book' | 'take' | 'closed' | 'settings';
/** The blotter's own action set: every one is a server call, never a local mutation. */
type PositionAct = 'breakeven' | 'trail' | 'trim' | 'close';

const inputClass =
  'rounded-lg border border-line/80 bg-ink-900/80 px-2.5 py-1.5 text-[11px] normal-case tracking-normal text-slate-200 outline-none placeholder:text-slate-600 focus:border-mint-400/50';

const actionTone: Record<string, string> = {
  HOLD: 'border-line/70 text-slate-400',
  STOP_BREACHED: 'border-rose-400/40 bg-rose-400/10 text-rose-400',
  TARGET_REACHED: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
  MOVE_STOP_TO_BREAKEVEN: 'border-amber-400/40 bg-amber-400/10 text-amber-400',
  TRAIL_STOP_EMA20: 'border-amber-400/40 bg-amber-400/10 text-amber-400',
  TIME_STOP_DUE: 'border-sky-400/40 bg-sky-400/10 text-sky-400',
};

const actionLabel: Record<string, string> = {
  HOLD: 'Hold',
  STOP_BREACHED: 'Stop breached',
  TARGET_REACHED: 'Target reached',
  MOVE_STOP_TO_BREAKEVEN: 'Raise stop',
  TRAIL_STOP_EMA20: 'Trail stop',
  TIME_STOP_DUE: 'Time stop due',
};

const horizonOptions = [
  { value: 'DAY', label: 'Day' },
  { value: 'WEEK', label: 'Week' },
  { value: 'MONTH', label: 'Month' },
  { value: 'YEAR', label: 'Year' },
];

const verdictOptions = [
  { value: 'TRADE', label: 'Trade' },
  { value: 'SMALL_SIZE', label: 'Small size' },
  { value: 'WATCH_ONLY', label: 'Watch only' },
];

function emptyForm(): OpenTradeInput {
  return {
    ticker: '',
    shares: 0,
    entryPrice: 0,
    stopLoss: 0,
    targetPrice: 0,
    horizon: 'WEEK',
    edgeVerdict: 'TRADE',
    notes: '',
  };
}

function pnlTone(value: number): string {
  if (value > 0) {
    return 'text-mint-400';
  }

  return value < 0 ? 'text-rose-400' : 'text-slate-300';
}

function Field({
  label,
  hint,
  children,
}: {
  label: string;
  hint?: string;
  children: ReactNode;
}) {
  return (
    <label className="flex flex-col gap-1">
      <span className="text-[10px] uppercase tracking-[0.16em] text-slate-500">{label}</span>
      {children}
      {hint ? <span className="text-[10px] text-slate-600">{hint}</span> : null}
    </label>
  );
}

function EquityCurve({ points }: { points: PortfolioEquityPoint[] }) {
  if (points.length < 2) {
    return (
      <p className="text-[11px] text-slate-500">
        The realized curve appears once the blotter has settled at least two trades.
      </p>
    );
  }

  const values = points.map((point) => point.equityPhp);
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;
  const path = points
    .map((point, index) => {
      const x = (index / (points.length - 1)) * 100;
      const y = 100 - ((point.equityPhp - min) / span) * 100;
      return `${x.toFixed(2)},${y.toFixed(2)}`;
    })
    .join(' ');

  return (
    <div>
      <svg viewBox="0 0 100 100" preserveAspectRatio="none" className="h-24 w-full">
        <polyline
          points={path}
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          vectorEffect="non-scaling-stroke"
          className="text-mint-400"
        />
      </svg>
      <div className="mt-1 flex justify-between text-[10px] text-slate-500">
        <span>{formatDate(points[0].date)}</span>
        <span className="tabular">
          {formatPrice(min)} – {formatPrice(max)}
        </span>
        <span>{formatDate(points[points.length - 1].date)}</span>
      </div>
    </div>
  );
}

function StatTile({
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
      <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500">{label}</p>
      <p className={cn('tabular mt-1 text-sm font-semibold text-slate-100', tone)}>{value}</p>
      {detail ? <p className="tabular mt-0.5 text-[10px] text-slate-500">{detail}</p> : null}
    </div>
  );
}

/**
 * Simple-mode position card: the same server fields as the blotter row, but laid out like a
 * consumer banking card instead of a wide trading table.
 */
function PositionCard({
  position,
  pending,
  onAct,
  showR = false,
}: {
  position: OpenPosition;
  pending: string | null;
  onAct: (position: OpenPosition, kind: PositionAct) => void;
  /** Pro mode adds the fee-paid R multiple the blotter table shows. */
  showR?: boolean;
}) {
  const action = describePositionAction(position.action);

  return (
    <li className="rounded-xl border border-line/70 bg-ink-850/70 p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold leading-tight text-white">
            {companyLabel(position.ticker, position.companyName)}
          </p>
          <p className="font-mono text-[10px] tracking-wider text-slate-500">
            {position.ticker} · {formatInteger(position.shares)} shares · bought {formatDate(position.openedDate)}
            {position.pyramidCount > 0 ? ` · +${position.pyramidCount} add` : ''}
            {showR ? ` · ${formatR(position.unrealizedNetR)} net` : ''}
          </p>
        </div>
        <div className="shrink-0 text-right">
          <p className={cn('tabular text-sm font-semibold', pnlTone(position.unrealizedNetPnlPhp))}>
            {formatPrice(position.unrealizedNetPnlPhp)}
          </p>
          <p className="text-[10px] text-slate-500">now {formatPrice(position.lastPrice)}</p>
        </div>
      </div>

      <dl className="mt-2 grid grid-cols-3 gap-2 text-[10px]">
        <div className="rounded-lg border border-line/60 bg-ink-900/60 px-2 py-1.5">
          <dt className="text-slate-500">Bought at</dt>
          <dd className="tabular mt-0.5 text-xs text-slate-200">{formatPrice(position.entryPrice)}</dd>
        </div>
        <div className="rounded-lg border border-line/60 bg-ink-900/60 px-2 py-1.5">
          <dt className="text-slate-500">Sell at</dt>
          <dd className="tabular mt-0.5 text-xs text-mint-400">{formatPrice(position.targetPrice)}</dd>
        </div>
        <div className="rounded-lg border border-line/60 bg-ink-900/60 px-2 py-1.5">
          <dt className="text-slate-500">Cut at</dt>
          <dd className="tabular mt-0.5 text-xs text-rose-400">{formatPrice(position.currentStopLoss)}</dd>
        </div>
      </dl>

      <div className="mt-2 flex flex-wrap items-center gap-1.5">
        <span
          className={cn(
            'inline-flex rounded border px-1.5 py-0.5 text-[9px] uppercase tracking-wider',
            actionTone[position.action] ?? 'border-line/70 text-slate-400',
          )}
        >
          {action.label}
        </span>
        <Button
          variant="secondary"
          className="ml-auto h-7 px-2 text-[9px]"
          busy={pending === `close-${position.id}`}
          disabled={pending !== null}
          onClick={() => onAct(position, 'close')}
        >
          Sell @ {formatPrice(position.lastPrice)}
        </Button>
        {position.recommendedTrimShares > 0 ? (
          <Button
            variant="ghost"
            className="h-7 px-2 text-[9px]"
            busy={pending === `trim-${position.id}`}
            disabled={pending !== null}
            onClick={() => onAct(position, 'trim')}
          >
            Trim {formatInteger(position.recommendedTrimShares)}
          </Button>
        ) : null}
        {position.currentStopLoss < position.breakEvenStopPrice ? (
          <Button
            variant="ghost"
            className="h-7 px-2 text-[9px]"
            busy={pending === `breakeven-${position.id}`}
            disabled={pending !== null}
            onClick={() => onAct(position, 'breakeven')}
          >
            Move stop to break-even
          </Button>
        ) : null}
        {position.ema20 != null && position.ema20 > position.currentStopLoss ? (
          <Button
            variant="ghost"
            className="h-7 px-2 text-[9px]"
            busy={pending === `trail-${position.id}`}
            disabled={pending !== null}
            onClick={() => onAct(position, 'trail')}
          >
            Raise stop with price
          </Button>
        ) : null}
      </div>

      <p className="mt-1.5 text-[11px] leading-relaxed text-slate-500">
        {position.actionNote || action.description}
      </p>
      {position.pyramidCount > 0 ? (
        <p className="mt-1 text-[10px] leading-relaxed text-slate-600">
          {describePyramidCount(position.pyramidCount)}
        </p>
      ) : null}
      {position.recommendedTrimShares > 0 ? (
        <p className="mt-1 text-[10px] leading-relaxed text-slate-600">
          {describeTrimGuidance(position.recommendedTrimShares, position.ticker)}
        </p>
      ) : null}
    </li>
  );
}

function BookTab({
  summary,
  pending,
  onAct,
  simple,
}: {
  summary: PortfolioSummary;
  pending: string | null;
  onAct: (position: OpenPosition, kind: PositionAct) => void;
  simple: boolean;
}) {
  const account = summary.account;

  // Simple mode keeps three plain headline numbers on screen and moves the guardrail math
  // into a drawer; Pro mode shows the whole panel at once.
  const guardrailTiles = (
    <>
      <StatTile
        label="Open risk"
        value={formatPrice(summary.openRiskPhp)}
        detail={`${formatPercent(summary.openHeatPct, 2, false)} of equity in play`}
        tone={summary.openHeatPct > account.maxPortfolioHeatPct ? 'text-amber-400' : undefined}
      />
      <StatTile
        label="Rules"
        value={`${account.maxRiskPerTradePct.toFixed(2)}% per trade`}
        detail={`${account.maxPortfolioHeatPct.toFixed(1)}% heat cap · ${account.maxPositionPct.toFixed(0)}% position cap`}
      />
      <StatTile
        label="Drawdown"
        value={formatPercent(summary.drawdownPct, 2, false)}
        detail={`peak ${formatPrice(summary.peakEquityPhp)} equity`}
        tone={
          summary.drawdownPct >= account.hardDrawdownThresholdPct
            ? 'text-rose-400'
            : summary.drawdownPct >= account.softDrawdownThresholdPct
              ? 'text-amber-400'
              : undefined
        }
      />
      <StatTile
        label="Risk posture"
        value={simple ? describeRiskPosture(summary.riskPosture) : summary.riskPosture}
        detail={
          summary.riskPosture === 'HALTED'
            ? 'new risk halted, manage exits only'
            : summary.riskPosture === 'DEFENSIVE'
              ? 'per-trade risk scaled down until the book repairs'
              : 'full risk budget available'
        }
        tone={
          summary.riskPosture === 'HALTED'
            ? 'text-rose-400'
            : summary.riskPosture === 'DEFENSIVE'
              ? 'text-amber-400'
              : 'text-mint-400'
        }
      />
    </>
  );

  return (
    <div className="mt-4">
      <dl className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-5">
        <StatTile
          label={simple ? 'Total value' : 'Total equity'}
          value={formatPrice(summary.totalEquityPhp)}
          detail={`${formatPrice(account.cashBalancePhp)} cash · ${formatPrice(summary.investedCapitalPhp)} invested`}
        />
        <StatTile
          label={simple ? 'Profit if sold today' : 'Unrealized'}
          value={formatPrice(summary.unrealizedNetPnlPhp)}
          detail={simple ? 'after broker fees' : 'mark-to-market, net of the sell leg'}
          tone={pnlTone(summary.unrealizedNetPnlPhp)}
        />
        <StatTile
          label={simple ? 'Profit so far' : 'Realized'}
          value={formatPrice(summary.realizedNetPnlPhp)}
          detail={`${formatInteger(summary.closedTrades)} ${simple ? 'closed' : 'settled'} · ${formatPercent(
            summary.winRate,
            1,
            false,
          )} ${simple ? 'winners' : 'win'}`}
          tone={pnlTone(summary.realizedNetPnlPhp)}
        />
        {simple ? null : guardrailTiles}
      </dl>

      {simple ? (
        <TechnicalDisclosure title="Risk & limits" className="mt-3">
          <dl className="grid grid-cols-2 gap-2 sm:grid-cols-3">{guardrailTiles}</dl>
        </TechnicalDisclosure>
      ) : null}

      {summary.sectorExposure && summary.sectorExposure.length > 0 ? (
        <div className="mt-3">
          <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500">Sector exposure</p>
          <ul className="mt-1 grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3">
            {summary.sectorExposure.map((sector) => {
              const used = sector.maxSectorExposurePct > 0
                ? Math.min(100, (sector.exposurePct / sector.maxSectorExposurePct) * 100)
                : 0;
              const breached = sector.exposurePct >= sector.maxSectorExposurePct;

              return (
                <li key={sector.sector} className="rounded-xl border border-line/70 bg-ink-850/80 px-3 py-2">
                  <div className="flex items-baseline justify-between gap-2">
                    <p className="truncate text-xs font-semibold text-slate-100" title={sector.sector}>
                      {sector.sector}
                    </p>
                    <p className={cn('tabular text-[11px]', breached ? 'text-rose-400' : 'text-slate-400')}>
                      {formatPercent(sector.exposurePct, 1, false)} /{' '}
                      {formatPercent(sector.maxSectorExposurePct, 0, false)}
                    </p>
                  </div>
                  <div className="mt-1.5 h-1 overflow-hidden rounded-full bg-ink-900">
                    <div
                      className={cn('h-full rounded-full', breached ? 'bg-rose-400' : 'bg-mint-400/80')}
                      style={{ width: `${used}%` }}
                    />
                  </div>
                  <p className="mt-1 text-[10px] text-slate-500">
                    {sector.positionCount} of {sector.maxPositionsPerSector} names
                  </p>
                </li>
              );
            })}
          </ul>
        </div>
      ) : null}

      {summary.alerts.length > 0 ? (
        <ul className="mt-3 space-y-1 rounded-xl border border-amber-400/30 bg-amber-400/5 p-3">
          {summary.alerts.map((alert) => (
            <li key={alert} className="text-[11px] leading-relaxed text-amber-400/90">
              {simple ? describePortfolioAlert(alert) : alert}
            </li>
          ))}
        </ul>
      ) : null}

      {summary.positions.length === 0 ? (
        <p className="mt-6 rounded-xl border border-line/60 bg-ink-850/50 px-4 py-8 text-center text-xs text-slate-500">
          {simple
            ? 'No trades yet. Open an advice card and press Practise This Trade — the desk fills in everything.'
            : 'No open positions. Take a qualifying pick from the signal table, or research any PSE name on demand.'}
        </p>
      ) : (
        <>
          <ul
            className={cn(
              'mt-4 grid gap-3 sm:grid-cols-2',
              simple ? undefined : 'md:hidden',
            )}
          >
            {summary.positions.map((position) => (
              <PositionCard
                key={position.id}
                position={position}
                pending={pending}
                onAct={onAct}
                showR={!simple}
              />
            ))}
          </ul>
          <div
            className={cn(
              'mt-4 overflow-x-auto rounded-xl border border-line/60',
              simple ? 'hidden' : 'hidden md:block',
            )}
          >
          <table className="w-full min-w-232 border-collapse text-left">
            <thead>
              <tr className="text-[10px] uppercase tracking-[0.16em] text-slate-500">
                <th className="sticky-col px-3 py-2.5 font-medium">Name</th>
                <th className="px-3 py-2.5 text-right font-medium">Shares</th>
                <th className="px-3 py-2.5 text-right font-medium">Entry</th>
                <th className="px-3 py-2.5 text-right font-medium">Last</th>
                <th className="px-3 py-2.5 text-right font-medium">Stop</th>
                <th className="px-3 py-2.5 text-right font-medium">Target</th>
                <th className="px-3 py-2.5 text-right font-medium">Net P&amp;L</th>
                {simple ? null : <th className="px-3 py-2.5 text-right font-medium">R</th>}
                <th className="px-3 py-2.5 font-medium">{simple ? 'What to do' : 'Exit signal'}</th>
                <th className="px-3 py-2.5 text-right font-medium">Actions</th>
              </tr>
            </thead>
            <tbody>

              {summary.positions.map((position) => (
                <tr key={position.id} className="border-t border-line/60 align-top">
                  <td className="sticky-col px-3 py-3">
                    <p className="font-mono text-xs font-semibold tracking-wide text-white">{position.ticker}</p>
                    <p className="mt-0.5 max-w-56 truncate text-[10px] text-slate-500">
                      {position.companyName || position.sector}
                    </p>
                    <p className="text-[10px] text-slate-600">
                      {position.horizon} · {formatDate(position.openedDate)} · {position.sessionsHeld} session
                      {position.sessionsHeld === 1 ? '' : 's'}
                      {position.pyramidCount > 0 ? ` · +${position.pyramidCount} add` : ''}
                    </p>
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-slate-300">
                    {formatInteger(position.shares)}
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-slate-300">
                    {formatPrice(position.entryPrice)}
                    <p className="text-[10px] text-slate-600">{formatPrice(position.buyCostPhp)} cost</p>
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-slate-100">
                    {formatPrice(position.lastPrice)}
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-rose-400/90">
                    {formatPrice(position.currentStopLoss)}
                    <p className="text-[10px] text-slate-600">open {formatPrice(position.initialStopLoss)}</p>
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-mint-400">
                    {formatPrice(position.targetPrice)}
                  </td>
                  <td
                    className={cn(
                      'tabular px-3 py-3 text-right text-xs font-semibold',
                      pnlTone(position.unrealizedNetPnlPhp),
                    )}
                  >
                    {formatPrice(position.unrealizedNetPnlPhp)}
                  </td>
                  {simple ? null : (
                    <td className={cn('tabular px-3 py-3 text-right text-xs', pnlTone(position.unrealizedNetR))}>
                      {formatR(position.unrealizedNetR)}
                    </td>
                  )}
                  <td className="px-3 py-3">
                    <Tooltip
                      label={
                        position.actionNote ||
                        (simple ? describePositionAction(position.action).description : actionLabel[position.action]) ||
                        position.action
                      }
                    >
                      <span
                        tabIndex={0}
                        className={cn(
                          'inline-flex rounded border px-1.5 py-0.5 text-[9px] uppercase tracking-wider',
                          actionTone[position.action] ?? 'border-line/70 text-slate-400',
                        )}
                      >
                        {simple
                          ? describePositionAction(position.action).label
                          : (actionLabel[position.action] ?? position.action)}
                      </span>
                    </Tooltip>
                    {position.action !== 'HOLD' && position.actionNote ? (
                      <p className="mt-1 max-w-60 text-[10px] leading-relaxed text-slate-500">
                        {position.actionNote}
                      </p>
                    ) : null}
                  </td>
                  <td className="px-3 py-3 text-right">
                    <div className="flex flex-col items-end gap-1">
                      {position.currentStopLoss < position.breakEvenStopPrice ? (
                        <Button
                          variant="ghost"
                          className="h-7 px-2 text-[9px]"
                          busy={pending === `breakeven-${position.id}`}
                          disabled={pending !== null}
                          onClick={() => onAct(position, 'breakeven')}
                        >
                          {simple ? 'Move stop to break-even' : 'Break-even'}
                        </Button>
                      ) : null}
                      {position.ema20 != null && position.ema20 > position.currentStopLoss ? (
                        <Button
                          variant="ghost"
                          className="h-7 px-2 text-[9px]"
                          busy={pending === `trail-${position.id}`}
                          disabled={pending !== null}
                          onClick={() => onAct(position, 'trail')}
                        >
                          {simple ? 'Raise stop with price' : 'Trail EMA20'}
                        </Button>
                      ) : null}
                      <Button
                        variant="secondary"
                        className="h-7 px-2 text-[9px]"
                        busy={pending === `close-${position.id}`}
                        disabled={pending !== null}
                        onClick={() => onAct(position, 'close')}
                      >
                        {simple ? 'Sell' : 'Close'} @ {formatPrice(position.lastPrice)}
                      </Button>
                      {position.recommendedTrimShares > 0 ? (
                        <Tooltip
                          label={`Banks ${formatInteger(
                            position.recommendedTrimShares,
                          )} shares in whole board lots and ratchets the remaining stop to break-even`}
                        >
                          <Button
                            variant="ghost"
                            className="h-7 px-2 text-[9px]"
                            busy={pending === `trim-${position.id}`}
                            disabled={pending !== null}
                            onClick={() => onAct(position, 'trim')}
                          >
                            {simple ? 'Bank half' : `Trim ${formatInteger(position.recommendedTrimShares)}`}
                          </Button>
                        </Tooltip>
                      ) : null}
                    </div>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
        </>
      )}

      <p className="mt-2 text-[10px] text-slate-600">
        {simple
          ? `Closing uses the latest stored price after broker fees. The desk blocks any order that breaks your cash or risk limits. Settings last saved ${formatTimestamp(account.updatedAtUtc)}.`
          : `Closing fills the latest stored close after the sell leg; the server refuses a stop above the last price and rejects any order that breaches the cash, heat or per-trade risk rules. Settings last saved ${formatTimestamp(account.updatedAtUtc)}.`}
      </p>
    </div>
  );
}


/** Phone-sized settled trade: the same fee-paid result, without a horizontal scroll. */
function ClosedTradeCard({ trade, simple }: { trade: ClosedTrade; simple: boolean }) {
  return (
    <li className="rounded-xl border border-line/70 bg-ink-850/70 p-3">
      <div className="flex items-start justify-between gap-3">
        <div className="min-w-0">
          <p className="truncate text-sm font-semibold leading-tight text-white">
            {companyLabel(trade.ticker, trade.companyName)}
          </p>
          <p className="font-mono text-[10px] tracking-wider text-slate-500">{trade.ticker}</p>
          <p className="mt-0.5 text-[10px] text-slate-500">
            {formatDate(trade.openedDate)} → {trade.closedDate ? formatDate(trade.closedDate) : '—'} ·{' '}
            {formatInteger(trade.shares)} shares
          </p>
        </div>
        <div className="shrink-0 text-right">
          <p className={cn('tabular text-sm font-semibold', pnlTone(trade.realizedNetPnlPhp))}>
            {formatPrice(trade.realizedNetPnlPhp)}
          </p>
          <p className="text-[10px] text-slate-500">
            {simple ? 'after fees' : `${formatR(trade.realizedNetR)} net`}
          </p>
        </div>
      </div>
      <p className="tabular mt-2 text-[10px] text-slate-400">
        Bought {formatPrice(trade.entryPrice)} · sold {formatPrice(trade.exitPrice)}
      </p>
      {trade.notes ? (
        <p className="mt-1 text-[10px] leading-relaxed text-slate-500">{trade.notes}</p>
      ) : null}
    </li>
  );
}

function ClosedTab({ summary, simple }: { summary: PortfolioSummary; simple: boolean }) {
  const closed: ClosedTrade[] = summary.closed;

  return (
    <div className="mt-4">
      <div className="grid gap-3 lg:grid-cols-[2fr_1fr]">
        <div className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
            {simple ? 'Your money over time' : 'Realized equity'}
          </h3>
          <div className="mt-2">
            <EquityCurve points={summary.realizedEquityCurve} />
          </div>
        </div>
        <div className="grid grid-cols-2 gap-2">
          <StatTile
            label={simple ? 'Trades closed' : 'Settled'}
            value={formatInteger(summary.closedTrades)}
            detail={`${formatPercent(summary.winRate, 1, false)} winners`}
          />
          <StatTile
            label={simple ? 'Profit banked' : 'Realized'}
            value={formatPrice(summary.realizedNetPnlPhp)}
            detail={simple ? 'after broker fees' : 'after both fee legs'}
            tone={pnlTone(summary.realizedNetPnlPhp)}
          />
          <StatTile
            label={simple ? 'Cash available' : 'Cash'}
            value={formatPrice(summary.account.cashBalancePhp)}
            detail="free to deploy"
          />
          <StatTile
            label={simple ? 'Total value' : 'Equity'}
            value={formatPrice(summary.totalEquityPhp)}
            detail="cash + marked positions"
          />
          <StatTile
            label={simple ? 'Average trade' : 'Expectancy'}
            value={formatR(summary.expectancyR)}
            detail={simple ? 'after broker fees' : 'per settled trade, in R'}
            tone={summary.expectancyR >= 0 ? 'text-mint-400' : 'text-rose-400'}
          />
          <StatTile
            label="Profit factor"
            value={summary.profitFactor.toFixed(2)}
            detail={simple ? 'wins vs losses · above 1.00 is good' : 'gross wins ÷ losses'}
          />
          <StatTile
            label={simple ? 'Winner vs loser size' : 'Payoff ratio'}
            value={formatRatio(summary.payoffRatio)}
            detail={simple ? 'average win ÷ average loss' : 'avg winner R ÷ avg loser R'}
          />
        </div>
      </div>

      {simple ? (
        <p className="mt-2 text-[10px] leading-relaxed text-slate-500">
          {describePayoffRatio(summary.payoffRatio)}
        </p>
      ) : null}

      {closed.length === 0 ? (
        <p className="mt-4 rounded-xl border border-line/60 bg-ink-850/50 px-4 py-8 text-center text-xs text-slate-500">
          {simple
            ? 'No closed trades yet. When you sell a trade it lands here with what you made after broker fees.'
            : 'Nothing settled yet. Close a position from the blotter and it lands here with its fee-paid result in R.'}
        </p>
      ) : (
        <>
          <ul className="mt-3 grid gap-2 md:hidden">
            {closed.map((trade) => (
              <ClosedTradeCard key={trade.id} trade={trade} simple={simple} />
            ))}
          </ul>
          <div className="mt-3 hidden overflow-x-auto rounded-xl border border-line/60 md:block">
            <table className="w-full min-w-176 border-collapse text-left">
              <thead>
                <tr className="text-[10px] uppercase tracking-[0.16em] text-slate-500">
                  <th className="sticky-col px-3 py-2.5 font-medium">{simple ? 'Stock' : 'Name'}</th>
                <th className="px-3 py-2.5 font-medium">{simple ? 'Held' : 'Window'}</th>
                <th className="px-3 py-2.5 text-right font-medium">Shares</th>
                <th className="px-3 py-2.5 text-right font-medium">{simple ? 'Bought at' : 'Entry'}</th>
                <th className="px-3 py-2.5 text-right font-medium">{simple ? 'Sold at' : 'Exit'}</th>
                <th className="px-3 py-2.5 text-right font-medium">{simple ? 'Profit' : 'Net P&amp;L'}</th>
                {simple ? null : <th className="px-3 py-2.5 text-right font-medium">R</th>}
                <th className="px-3 py-2.5 font-medium">Notes</th>
              </tr>
            </thead>
            <tbody>
              {closed.map((trade) => (
                <tr key={trade.id} className="border-t border-line/60">
                  <td className="sticky-col px-3 py-2.5">
                    <p className="text-xs font-semibold leading-tight text-white">
                      {companyLabel(trade.ticker, trade.companyName)}
                    </p>
                    <p className="max-w-48 truncate font-mono text-[10px] text-slate-500">
                      {trade.ticker} · {trade.companyName}
                    </p>
                  </td>
                  <td className="px-3 py-2.5 text-[10px] text-slate-400">
                    {formatDate(trade.openedDate)} → {trade.closedDate ? formatDate(trade.closedDate) : '—'}
                  </td>
                  <td className="tabular px-3 py-2.5 text-right text-xs text-slate-300">
                    {formatInteger(trade.shares)}
                  </td>
                  <td className="tabular px-3 py-2.5 text-right text-xs text-slate-300">
                    {formatPrice(trade.entryPrice)}
                  </td>
                  <td className="tabular px-3 py-2.5 text-right text-xs text-slate-100">
                    {formatPrice(trade.exitPrice)}
                  </td>
                  <td
                    className={cn(
                      'tabular px-3 py-2.5 text-right text-xs font-semibold',
                      pnlTone(trade.realizedNetPnlPhp),
                    )}
                  >
                    {formatPrice(trade.realizedNetPnlPhp)}
                  </td>
                  {simple ? null : (
                    <td className={cn('tabular px-3 py-2.5 text-right text-xs', pnlTone(trade.realizedNetR ?? 0))}>
                      {formatR(trade.realizedNetR)}
                    </td>
                  )}
                  <td className="max-w-[16rem] px-3 py-2.5 text-[10px] leading-relaxed text-slate-500">
                    {trade.notes || '—'}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
        </>
      )}
    </div>
  );
}


function SettingsTab({
  account,
  pending,
  onSave,
  simple,
}: {
  account: PortfolioAccount;
  pending: string | null;
  onSave: (update: PortfolioSettingsUpdate) => void;
  simple: boolean;
}) {
  const [form, setForm] = useState<PortfolioSettingsUpdate>({
    startingCapitalPhp: account.startingCapitalPhp,
    cashBalancePhp: account.cashBalancePhp,
    maxRiskPerTradePct: account.maxRiskPerTradePct,
    smallSizeRiskMultiplier: account.smallSizeRiskMultiplier,
    maxPositionPct: account.maxPositionPct,
    maxPortfolioHeatPct: account.maxPortfolioHeatPct,
    maxSectorExposurePct: account.maxSectorExposurePct,
    maxPositionsPerSector: account.maxPositionsPerSector,
    softDrawdownThresholdPct: account.softDrawdownThresholdPct,
    hardDrawdownThresholdPct: account.hardDrawdownThresholdPct,
    targetAnnualVolPct: account.targetAnnualVolPct,
  });

  useEffect(() => {
    setForm({
      startingCapitalPhp: account.startingCapitalPhp,
      cashBalancePhp: account.cashBalancePhp,
      maxRiskPerTradePct: account.maxRiskPerTradePct,
      smallSizeRiskMultiplier: account.smallSizeRiskMultiplier,
      maxPositionPct: account.maxPositionPct,
      maxPortfolioHeatPct: account.maxPortfolioHeatPct,
      maxSectorExposurePct: account.maxSectorExposurePct,
      maxPositionsPerSector: account.maxPositionsPerSector,
      softDrawdownThresholdPct: account.softDrawdownThresholdPct,
      hardDrawdownThresholdPct: account.hardDrawdownThresholdPct,
      targetAnnualVolPct: account.targetAnnualVolPct,
    });
  }, [account]);

  const numberField = (key: keyof PortfolioSettingsUpdate, label: string, hint: string) => (
    <Field key={key} label={label} hint={hint}>
      <input
        type="number"
        inputMode="decimal"
        step="0.01"
        min="0"
        value={form[key] ?? 0}
        onChange={(event) =>
          setForm((current) => ({ ...current, [key]: Number(event.target.value) || 0 }))
        }
        className={inputClass}
      />
    </Field>
  );

  return (
    <div className="mt-4">
      <p className="max-w-3xl text-[11px] leading-relaxed text-slate-400">
        {simple
          ? 'These are the limits the desk uses when it decides how many shares to suggest. You can adjust how much you are willing to risk on each trade, how much of your money can sit in one stock or one sector, and when the desk should slow down after a losing streak. The server applies them to every new order.'
          : "These are the only knobs the desk honours. The server sizes every ticket against the stored equity with the per-trade risk fraction (halved for SMALL_SIZE), scales that fraction by the edge tier, the name's beta and the book's drawdown, caps any single position at the position limit, holds each sector under its exposure cap and refuses an order that would push total open heat past the heat cap. Cash is decremented on entry and credited with the net sell proceeds on exit."}
      </p>

      <p className="mt-3 text-[10px] uppercase tracking-[0.18em] text-slate-500">
        {simple ? 'How much you risk' : 'Sizing'}
      </p>
      {simple ? (
        <div className="mt-1 space-y-3 rounded-xl border border-line/70 bg-ink-850/60 p-3">
          <div>
            <p className="text-[11px] font-semibold text-slate-200">Play money</p>
            <div className="mt-1.5 flex flex-wrap gap-1.5">
              {BUDGET_PRESETS_PHP.map((amount) => {
                const active = Math.abs((form.startingCapitalPhp ?? 0) - amount) < 1;
                return (
                  <button
                    key={amount}
                    type="button"
                    aria-pressed={active}
                    onClick={() =>
                      setForm((current) => ({
                        ...current,
                        startingCapitalPhp: amount,
                        cashBalancePhp: amount,
                      }))
                    }
                    className={cn(
                      'rounded-full border px-3 py-1 text-[11px] font-medium transition',
                      active
                        ? 'border-mint-400/60 bg-mint-400/10 text-mint-400'
                        : 'border-line/80 bg-ink-900 text-slate-300 hover:border-mint-400/40 hover:text-mint-400',
                    )}
                  >
                    {formatPrice(amount)}
                  </button>
                );
              })}
            </div>
            <p className="mt-1 text-[10px] text-slate-600">
              Practice cash resets to the amount you pick. Nothing here touches real money.
            </p>
          </div>

          <div>
            <p className="text-[11px] font-semibold text-slate-200">How safe do you want to play it?</p>
            <div className="mt-1.5 grid gap-2 sm:grid-cols-3">
              {SAFETY_PRESETS.map((preset) => {
                const active = safetyPresetForSettings(form)?.id === preset.id;
                return (
                  <button
                    key={preset.id}
                    type="button"
                    aria-pressed={active}
                    onClick={() => setForm((current) => ({ ...current, ...preset.settings }))}
                    className={cn(
                      'rounded-xl border px-3 py-2 text-left transition',
                      active
                        ? 'border-mint-400/60 bg-mint-400/10'
                        : 'border-line/80 bg-ink-900 hover:border-mint-400/40',
                    )}
                  >
                    <p className={cn('text-[11px] font-semibold', active ? 'text-mint-400' : 'text-slate-200')}>
                      {preset.label}
                    </p>
                    <p className="mt-0.5 text-[10px] leading-relaxed text-slate-500">{preset.tagline}</p>
                  </button>
                );
              })}
            </div>
            <p className="mt-1 text-[10px] leading-relaxed text-slate-600">
              {safetyPresetForSettings(form)?.detail ??
                'Custom rules. The server still checks every value before it saves.'}
            </p>
          </div>
        </div>
      ) : null}
      <div className="mt-1 grid gap-3 rounded-xl border border-line/70 bg-ink-850/60 p-3 sm:grid-cols-2 lg:grid-cols-3">
        {numberField('startingCapitalPhp', 'Starting capital', 'reference equity, PHP')}
        {numberField('cashBalancePhp', 'Cash balance', 'what the next order can spend, PHP')}
        {numberField('maxRiskPerTradePct', 'Risk per trade', 'percent of equity at the stop, e.g. 1.00')}
        {numberField(
          'smallSizeRiskMultiplier',
          simple ? 'Small-position factor' : 'SMALL_SIZE factor',
          'multiplier applied to the risk budget',
        )}
        {numberField('maxPositionPct', 'Max position', 'per-name cap on the buy outlay, percent of equity')}
        {numberField('maxPortfolioHeatPct', 'Max portfolio heat', 'total open risk allowed, percent of equity')}
      </div>

      <p className="mt-3 text-[10px] uppercase tracking-[0.18em] text-slate-500">
        {simple ? 'Safety limits' : 'Pod-shop guardrails'}
      </p>
      <div className="mt-1 grid gap-3 rounded-xl border border-line/70 bg-ink-850/60 p-3 sm:grid-cols-2 lg:grid-cols-3">
        {numberField('maxSectorExposurePct', 'Max sector exposure', 'percent of equity in one sector')}
        {numberField('maxPositionsPerSector', 'Names per sector', 'max open positions in one sector')}
        {numberField('softDrawdownThresholdPct', 'Soft drawdown', 'risk halved past this drawdown, percent')}
        {numberField('hardDrawdownThresholdPct', 'Hard drawdown', 'new risk halted past this drawdown, percent')}
        {numberField(
          'targetAnnualVolPct',
          simple ? 'Target yearly swing' : 'Target annual vol',
          'book volatility the budget is scaled to',
        )}
      </div>

      <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
        <p className="text-[10px] text-slate-600">
          {simple
            ? `Saved ${formatTimestamp(account.updatedAtUtc)} · the server checks every value, so a bad combination is rejected instead of quietly breaking your book.`
            : `Saved ${formatTimestamp(account.updatedAtUtc)} · validated server-side, so a bad combination comes back as a 400 instead of silently changing the book.`}
        </p>
        <Button
          variant="primary"
          busy={pending === 'settings'}
          disabled={pending !== null}
          onClick={() => onSave(form)}
        >
          {simple ? 'Save My Rules' : 'Save settings'}
        </Button>
      </div>
    </div>
  );
}


function TakeTradeTab({
  form,
  pending,
  account,
  onChange,
  onSubmit,
  simple,
}: {
  form: OpenTradeInput;
  pending: string | null;
  account: PortfolioAccount;
  onChange: (patch: Partial<OpenTradeInput>) => void;
  onSubmit: () => void;
  simple: boolean;
}) {
  const gross = Math.max(0, form.shares) * Math.max(0, form.entryPrice);
  const grossRisk = Math.max(0, form.shares) * Math.max(0, form.entryPrice - form.stopLoss);
  const valid =
    form.ticker.trim().length > 0 &&
    form.shares > 0 &&
    form.entryPrice > 0 &&
    form.stopLoss > 0 &&
    form.stopLoss < form.entryPrice &&
    form.targetPrice > form.entryPrice;

  return (
    <div className="mt-4">
      <p className="max-w-3xl text-[11px] leading-relaxed text-slate-400">
        {simple
          ? 'This records a trade you have decided to take. The desk double-checks the shares, your available cash and your risk limits before saving anything.'
          : 'Log a position the desk already sized. Shares, entry, stop and target come from the server ticket — the desk re-checks the lot size, the tick grid, available cash, the per-name cap and total heat before it writes anything, and the entry is priced at the planned level with the buy leg charged to cash.'}
      </p>

      <div className="mt-3 grid gap-3 rounded-xl border border-line/70 bg-ink-850/60 p-3 sm:grid-cols-2 lg:grid-cols-3">
        <Field label="Ticker" hint="PSE symbol, e.g. SM, BDO, ALI">
          <input
            type="text"
            value={form.ticker}
            onChange={(event) => onChange({ ticker: event.target.value.toUpperCase() })}
            placeholder="TICKER"
            className={inputClass}
          />
        </Field>
        <Field label="Shares" hint="must be a multiple of the board lot">
          <input
            type="number"
            inputMode="numeric"
            step="1"
            min="0"
            value={form.shares}
            onChange={(event) => onChange({ shares: Math.trunc(Number(event.target.value) || 0) })}
            className={inputClass}
          />
        </Field>
        <Field label="Horizon">
          <Select
            ariaLabel="Position horizon"
            value={form.horizon}
            options={horizonOptions}
            className="w-full"
            onChange={(value) => onChange({ horizon: value as Horizon })}
          />
        </Field>
        <Field label="Entry price" hint="tick-aligned planned fill">
          <input
            type="number"
            inputMode="decimal"
            step="0.0001"
            min="0"
            value={form.entryPrice}
            onChange={(event) => onChange({ entryPrice: Number(event.target.value) || 0 })}
            className={inputClass}
          />
        </Field>
        <Field label="Stop loss" hint="below entry; the stop defines the risk">
          <input
            type="number"
            inputMode="decimal"
            step="0.0001"
            min="0"
            value={form.stopLoss}
            onChange={(event) => onChange({ stopLoss: Number(event.target.value) || 0 })}
            className={inputClass}
          />
        </Field>
        <Field label="Target" hint="above entry; the ATR-shaped objective">
          <input
            type="number"
            inputMode="decimal"
            step="0.0001"
            min="0"
            value={form.targetPrice}
            onChange={(event) => onChange({ targetPrice: Number(event.target.value) || 0 })}
            className={inputClass}
          />
        </Field>
        <Field label="Verdict" hint="SMALL_SIZE halves the risk budget">
          <Select
            ariaLabel="Edge verdict"
            value={form.edgeVerdict}
            options={verdictOptions}
            className="w-full"
            onChange={(value) => onChange({ edgeVerdict: value as OpenTradeInput['edgeVerdict'] })}
          />
        </Field>
        <Field label="Notes">
          <input
            type="text"
            value={form.notes ?? ''}
            onChange={(event) => onChange({ notes: event.target.value })}
            placeholder="fill, slippage, why"
            className={inputClass}
          />
        </Field>
      </div>

      <dl className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-4">
        <StatTile label="Gross outlay" value={formatPrice(gross)} detail={`${formatInteger(form.shares)} sh × entry`} />
        <StatTile label="Gross risk" value={formatPrice(grossRisk)} detail="shares × stop distance" />
        <StatTile label="Cash available" value={formatPrice(account.cashBalancePhp)} detail="before the fee leg" />
        <StatTile
          label="Heat cap"
          value={`${account.maxPortfolioHeatPct.toFixed(1)}%`}
          detail={`${account.maxRiskPerTradePct.toFixed(2)}% risk per trade`}
        />
      </dl>

      {!valid ? (
        <p className="mt-2 text-[10px] text-amber-400/90">
          The ticket needs a ticker, whole shares, and entry &gt; stop &gt; 0 with a target above entry.
        </p>
      ) : null}

      <div className="mt-3 flex items-center justify-end">
        <Button
          variant="primary"
          busy={pending === 'take'}
          disabled={pending !== null || !valid}
          onClick={onSubmit}
        >
          {simple ? 'Record This Trade' : 'Record in blotter'}
        </Button>
      </div>
    </div>
  );
}


/**
 * The desk's paper blotter: capital settings, mark-to-market open positions with the
 * server's own exit instructions, the closed ledger and the take-trade form. Every peso
 * figure, action and rejection comes from the API; nothing is sized in the browser.
 */
export function PortfolioDialog({
  open,
  onOpenChange,
  draft,
  onDraftConsumed,
  summary: sharedSummary,
  onPortfolioMutated,
  viewMode = 'simple',
}: PortfolioDialogProps) {
  const simple = viewMode !== 'pro';
  const [localSummary, setLocalSummary] = useState<PortfolioSummary | null>(null);
  const summary = sharedSummary ?? localSummary;
  const [status, setStatus] = useState<LoadStatus>('idle');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [tab, setTab] = useState<TabKey>('book');
  const [pending, setPending] = useState<string | null>(null);
  const [form, setForm] = useState<OpenTradeInput>(emptyForm);

  const load = useCallback(async (force = false) => {
    // A fresh cache hit is a real answer: the terminal already fetched this book, so
    // re-opening the sheet must not fire another `/api/portfolio` call.
    if (!force && isQueryFresh(queryKeys.portfolioSummary)) {
      const cached = getSnapshot<PortfolioSummary>(queryKeys.portfolioSummary);

      if (cached) {
        setLocalSummary(cached);
        setStatus('ready');
        setError(null);
        return;
      }
    }

    setStatus('loading');

    try {
      const result = await fetchQuery({
        key: queryKeys.portfolioSummary,
        staleTimeMs: queryStaleTime.portfolio,
        force,
        fetcher: (signal) => portfolioApi.summary(signal),
      });
      setLocalSummary(result);
      setStatus('ready');
      setError(null);
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'Could not load the blotter.');
      setStatus('error');
    }
  }, []);

  useEffect(() => {
    if (!open) {
      return;
    }

    void load();
  }, [open, load]);

  useEffect(() => {
    if (!open || !draft) {
      return;
    }

    setForm({
      ticker: draft.ticker,
      shares: draft.shares,
      entryPrice: draft.entryPrice,
      stopLoss: draft.stopLoss,
      targetPrice: draft.targetPrice,
      horizon: draft.horizon,
      edgeVerdict: draft.edgeVerdict,
      companyName: draft.companyName,
      sector: draft.sector,
      signalItemId: draft.signalItemId,
      notes: '',
    });
    setTab('take');
    setNotice(null);
    onDraftConsumed();
  }, [open, draft, onDraftConsumed]);

  async function mutate(
    key: string,
    action: () => Promise<unknown>,
    message: string,
  ): Promise<boolean> {
    setPending(key);
    setError(null);
    setNotice(null);

    try {
      await action();

      // Exactly one portfolio read per mutation. The server has already recomputed the
      // order tickets and alerts, so this payload is authoritative for the shell too.
      const result = await portfolioApi.summary();
      setQueryData(queryKeys.portfolioSummary, result, queryStaleTime.portfolio);
      setLocalSummary(result);
      setStatus('ready');

      // Server-computed tickets in the visible run and any cached research now differ.
      invalidateQueries('signals:');
      invalidateQueries('research:item:');
      onPortfolioMutated?.(result);

      setNotice(message);
      return true;
    } catch (caught) {
      setError(caught instanceof Error ? caught.message : 'The request failed.');
      return false;
    } finally {
      setPending(null);
    }
  }

  function handleAct(position: OpenPosition, kind: PositionAct) {
    if (kind === 'trim') {
      // The server only recommends a trim it can settle in whole board lots on both legs; a
      // zero means "nothing legal to bank", so the buttons are hidden and this is a no-op.
      const shares = position.recommendedTrimShares;
      if (shares <= 0) {
        return;
      }

      void mutate(
        `trim-${position.id}`,
        () => portfolioApi.trimTrade(position.id, shares, position.lastPrice),
        `Banked ${formatInteger(shares)} of ${formatInteger(position.shares)} ${position.ticker} shares at ${formatPrice(
          position.lastPrice,
        )} — the rest keeps running.`,
      );
      return;
    }

    if (kind === 'close') {
      void mutate(
        `close-${position.id}`,
        () => portfolioApi.closeTrade(position.id, position.lastPrice),
        `${position.ticker} closed at ${formatPrice(position.lastPrice)}.`,
      );
      return;
    }

    const stop =
      kind === 'breakeven' ? position.breakEvenStopPrice : (position.ema20 ?? position.currentStopLoss);
    void mutate(
      `${kind}-${position.id}`,
      () => portfolioApi.updateStop(position.id, stop),
      `${position.ticker} stop moved to ${formatPrice(stop)}.`,
    );
  }

  function submitTrade() {
    const payload: OpenTradeInput = {
      ...form,
      ticker: form.ticker.trim().toUpperCase(),
      shares: Math.trunc(form.shares),
    };

    void mutate(
      'take',
      () => portfolioApi.openTrade(payload),
      `${payload.ticker} logged: ${formatInteger(payload.shares)} shares at ${formatPrice(payload.entryPrice)}.`,
    ).then((ok) => {
      if (ok) {
        setTab('book');
      }
    });
  }

  const account = summary?.account ?? null;


  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="xl">
        <DialogStickyHeader>
          <div className="min-w-0">
            <DialogTitle className="text-lg font-semibold text-white">
              {simple ? 'My Trades (Paper Tracker)' : 'Portfolio blotter'}
            </DialogTitle>
            <DialogDescription className="mt-1 max-w-2xl text-xs leading-relaxed text-slate-400">
              {simple
                ? 'A practice account that tracks the plans you record from the advice cards. It shows what the paper book holds, what it is worth today and the exit posture the server derived for each position.'
                : "Server-sized paper book: cash, open heat, fee-paid P&L and the exit instruction the session's OHLC implies. Sizing, cash and the risk caps are enforced by the API, never by this screen."}
            </DialogDescription>
          </div>
          <DialogClose asChild>
            <Button variant="ghost">Close</Button>
          </DialogClose>
        </DialogStickyHeader>

        <div className="mt-3 flex flex-col gap-2 sm:flex-row sm:flex-wrap sm:items-center sm:justify-between sm:gap-3">
          <Tabs
            value={tab}
            onValueChange={(value) => setTab(value as TabKey)}
            className="min-w-0 max-w-full"
          >
            <TabsList>
              <TabsTrigger value="book">
                {simple ? 'My Trades' : 'Book'}
                {summary ? ` · ${summary.openPositions}` : ''}
              </TabsTrigger>
              <TabsTrigger value="take">Record a trade</TabsTrigger>
              <TabsTrigger value="closed">
                {simple ? 'Closed Trades' : 'Ledger'}
                {summary ? ` · ${summary.closedTrades}` : ''}
              </TabsTrigger>
              <TabsTrigger value="settings">{simple ? 'My Rules' : 'Settings'}</TabsTrigger>
            </TabsList>
          </Tabs>
          <Button
            variant="secondary"
            className="h-8 w-full px-2.5 text-[10px] sm:w-auto"
            busy={status === 'loading'}
            disabled={pending !== null}
            onClick={() => void load(true)}
          >
            Refresh
          </Button>
        </div>

        {notice ? (
          <p className="mt-3 rounded-lg border border-mint-400/30 bg-mint-400/5 px-3 py-2 text-[11px] text-mint-400">
            {notice}
          </p>
        ) : null}

        {error ? (
          <p className="mt-3 rounded-lg border border-rose-400/30 bg-rose-400/5 px-3 py-2 text-[11px] text-rose-400">
            {error}
          </p>
        ) : null}

        {status === 'loading' && !summary ? (
          <p className="mt-6 text-xs text-slate-500">Loading the book…</p>
        ) : null}

        {summary && account ? (
          <>
            {tab === 'book' ? <BookTab summary={summary} pending={pending} onAct={handleAct} simple={simple} /> : null}
            {tab === 'take' ? (
              <TakeTradeTab
                form={form}
                pending={pending}
                account={account}
                onChange={(patch) => setForm((current) => ({ ...current, ...patch }))}
                onSubmit={submitTrade}
                simple={simple}
              />
            ) : null}
            {tab === 'closed' ? <ClosedTab summary={summary} simple={simple} /> : null}
            {tab === 'settings' ? (
              <SettingsTab
                account={account}
                pending={pending}
                simple={simple}
                onSave={(update) =>
                  void mutate('settings', () => portfolioApi.updateSettings(update), 'Portfolio settings saved.')
                }
              />
            ) : null}
          </>
        ) : null}
      </DialogContent>
    </Dialog>
  );
}

