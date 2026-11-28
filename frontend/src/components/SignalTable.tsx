import type { Horizon, SignalItem } from '../types/signals';
import { cn } from '../lib/cn';
import {
  formatCompactPeso,
  formatDeltaPercent,
  formatInteger,
  formatPrice,
  formatRatio,
} from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import { describePriceDeltaTone } from '../lib/plainAdvice';
import { computeRiskSizing, formatProbability, formatR } from '../lib/riskSizing';
import { preloadTickerDetailDialog } from '../lib/preloaders';
import { DriftBadge, driftAccent } from './DriftBadge';
import { VerdictBadge, verdictDescription } from './VerdictBadge';
import { Tooltip } from './ui/Tooltip';

interface SignalTableProps {
  items: SignalItem[];
  horizon: Horizon;
  onSelect: (ticker: string) => void;
  /** Logs the pick's server ticket in the blotter. */
  onTakeTrade: (item: SignalItem) => void;
  /** Cash the server wants held after funding the actionable picks; shown in the footer. */
  recommendedCashPct?: number | null;
}

function ConvictionBar({ score }: { score: number }) {
  const width = `${Math.max(4, Math.min(100, score))}%`;

  return (
    <div className="flex items-center gap-2">
      <div className="h-1.5 w-16 overflow-hidden rounded-full bg-ink-700">
        <div
          className="h-full rounded-full bg-linear-to-r from-mint-500 to-mint-400"
          style={{ width }}
        />
      </div>
      <span className="tabular type-caption font-semibold text-fg">{Math.round(score)}</span>
    </div>
  );
}

function rsiTone(value: number | undefined): string {
  if (value == null) {
    return 'text-fg-subtle';
  }

  if (value > 72) {
    return 'text-amber-400';
  }

  if (value < 40) {
    return 'text-rose-400';
  }

  return 'text-fg';
}

interface ProSignalCardProps {
  item: SignalItem;
  horizon: Horizon;
  onSelect: (ticker: string) => void;
  onTakeTrade: (item: SignalItem) => void;
}

/**
 * The phone-sized version of one table row. It carries the same server figures — rank,
 * drift, verdict, odds, entry/stop/target, net R:R and the board-lot ticket — but stacked
 * into a card so nothing is hidden behind a horizontal scroll on a 360px screen.
 */
function ProSignalCard({ item, horizon, onSelect, onTakeTrade }: ProSignalCardProps) {
  const sizing = computeRiskSizing(item);
  const closePrice =
    item.metrics && Number.isFinite(item.metrics.closePrice) && item.metrics.closePrice > 0
      ? item.metrics.closePrice
      : null;
  const changePct =
    closePrice != null &&
    typeof item.metrics?.changePct === 'number' &&
    Number.isFinite(item.metrics.changePct)
      ? item.metrics.changePct
      : null;
  const openDetails = () => onSelect(item.ticker);

  return (
    // The whole card opens the detail modal, like the desktop row: a phone reader should not
    // have to find the ticker or the Details button. The Take action and the trap-flag badge
    // stop propagation so they keep their own behaviour.
    <li
      onClick={openDetails}
      className="cursor-pointer px-4 py-3.5 transition hover:bg-ink-800/50"
    >
      <div className="flex items-start justify-between gap-2">
        <div className="flex min-w-0 items-start gap-2">
          <span className="tabular type-caption mt-0.5 w-6 shrink-0 font-semibold text-fg-subtle">
            {String(item.rank).padStart(2, '0')}
          </span>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-1.5">
              <button
                type="button"
                onClick={openDetails}
                onPointerEnter={() => preloadTickerDetailDialog(item.ticker, horizon)}
                onFocus={() => preloadTickerDetailDialog(item.ticker, horizon)}
                className="font-mono type-lead font-semibold text-fg transition hover:text-mint-400"
              >
                {item.ticker}
              </button>
              <span className="type-badge rounded border border-line/70 px-1.5 py-0.5 text-fg-subtle">
                {item.sector}
              </span>
              <DriftBadge status={item.driftStatus} previousRank={item.previousRank} />
            </div>
            <p className="type-caption mt-1 truncate text-fg-subtle">{item.companyName}</p>
            {item.trapFlags && item.trapFlags.length > 0 ? (
              <Tooltip
                label={
                  <span>
                    Disclosure classifier flags: {item.trapFlags.join(', ')}. Hard traps are filtered
                    out server-side; these are the remaining warnings.
                  </span>
                }
              >
                <span
                  tabIndex={0}
                  onClick={(event) => event.stopPropagation()}
                  className="type-badge mt-1.5 inline-flex items-center gap-1 rounded border border-amber-400/40 bg-amber-400/10 px-1.5 py-0.5 text-amber-400"
                >
                  {item.trapFlags.length} flag{item.trapFlags.length === 1 ? '' : 's'}
                </span>
              </Tooltip>
            ) : null}
          </div>
        </div>
        <div className="flex shrink-0 flex-col items-end gap-1">
          <Tooltip label={verdictDescription(item.edgeVerdict)}>
            <span tabIndex={0}>
              <VerdictBadge verdict={item.edgeVerdict} />
            </span>
          </Tooltip>
          <ConvictionBar score={item.convictionScore} />
        </div>
      </div>

      <dl className="mt-2.5 grid grid-cols-2 gap-1.5 xs:grid-cols-4">
        <div className="rounded-lg border border-line/60 bg-ink-850/60 px-2.5 py-2">
          <dt className="type-eyebrow text-fg-subtle">Close</dt>
          <dd className="tabular type-caption mt-0.5 font-semibold text-fg">
            {formatPrice(closePrice)}
          </dd>
          {changePct != null ? (
            <dd className={cn('tabular text-2xs', describePriceDeltaTone(changePct))}>
              {formatDeltaPercent(changePct, 2)}
            </dd>
          ) : null}
        </div>
        <div className="rounded-lg border border-line/60 bg-ink-850/60 px-2.5 py-2">
          <dt className="type-eyebrow text-fg-subtle">Entry</dt>
          <dd className="tabular type-caption mt-0.5 font-semibold text-fg">
            {formatPrice(item.entryPrice)}
          </dd>
        </div>
        <div className="rounded-lg border border-line/60 bg-ink-850/60 px-2.5 py-2">
          <dt className="type-eyebrow text-fg-subtle">Stop</dt>
          <dd className="tabular type-caption mt-0.5 font-semibold text-rose-400/90">
            {formatPrice(item.stopLoss)}
          </dd>
        </div>
        <div className="rounded-lg border border-line/60 bg-ink-850/60 px-2.5 py-2">
          <dt className="type-eyebrow text-fg-subtle">Target</dt>
          <dd className="tabular type-caption mt-0.5 font-semibold text-mint-400">
            {formatPrice(item.targetPrice)}
          </dd>
        </div>
      </dl>

      <div className="mt-2.5 flex flex-wrap items-center justify-between gap-2">
        <p className="tabular type-caption text-fg-subtle">
          <span
            className={cn(
              'font-semibold',
              item.winProbability >= item.breakEvenWinRate ? 'text-mint-400' : 'text-rose-400',
            )}
          >
            {formatProbability(item.winProbability)} win
          </span>{' '}
          · {formatR(item.expectedValueR)} EV · {formatRatio(item.netRiskRewardRatio)} net R:R ·{' '}
          {sizing.shares > 0 ? `${formatInteger(sizing.shares)} sh` : 'no lot'}
        </p>
        <div className="flex items-center gap-1.5">
          <button
            type="button"
            onClick={openDetails}
            className="tap type-caption rounded-lg border border-line/80 px-2.5 py-1.5 font-semibold text-fg-muted transition hover:border-mint-400/60 hover:text-fg"
          >
            Details
          </button>
          <button
            type="button"
            disabled={sizing.shares <= 0}
            onClick={(event) => {
              event.stopPropagation();
              onTakeTrade(item);
            }}
            className="tap type-caption rounded-lg border border-mint-400/40 px-2.5 py-1.5 font-semibold text-mint-400 transition hover:bg-mint-400/10 disabled:cursor-not-allowed disabled:border-line/60 disabled:text-fg-faint"
          >
            Take
          </button>
        </div>
      </div>
    </li>
  );
}

export function SignalTable({ items, horizon, onSelect, onTakeTrade, recommendedCashPct }: SignalTableProps) {
  const meta = horizonMeta[horizon];
  const tradeCount = items.filter((item) => item.edgeVerdict === 'TRADE').length;
  const smallSizeCount = items.filter((item) => item.edgeVerdict === 'SMALL_SIZE').length;
  const watchOnlyCount = items.filter((item) => item.edgeVerdict === 'WATCH_ONLY').length;
  const fundedPicks = items.filter((item) => item.edgeVerdict !== 'WATCH_ONLY').length;

  return (
    <section className="panel overflow-hidden">
      <header className="flex flex-wrap items-end justify-between gap-x-4 gap-y-2 border-b border-line/70 px-4 py-4 sm:px-5">
        <div>
          <h2 className="type-title text-fg">{meta.bookTitle}</h2>
          <p className="type-caption measure mt-1.5 text-fg-subtle">{meta.mandate}</p>
        </div>
        <span className="type-eyebrow hidden text-fg-subtle sm:block">
          {items.length} active names · edge-sized plans
        </span>
      </header>

      {items.length === 0 ? (
        <p className="type-body measure mx-auto px-4 py-12 text-center text-fg-subtle">
          This run returned no qualifying names. Re-run the pipeline after the next close.
        </p>
      ) : (
        <>
          {/* Phone: one card per pick, every server figure still on screen. */}
          <ul className="divide-y divide-line/60 md:hidden">
            {items.map((item) => (
              <ProSignalCard
                key={item.ticker}
                item={item}
                horizon={horizon}
                onSelect={onSelect}
                onTakeTrade={onTakeTrade}
              />
            ))}
          </ul>

          <div className="scroll-x hidden md:block">
            <table className="w-full min-w-268 border-collapse text-left">
            <thead>
              <tr className="type-eyebrow text-fg-subtle">
                <th className="sticky-col w-12 px-3 py-2.5 font-medium">#</th>
                <th className="sticky-col-2 px-3 py-2.5 font-medium">Name</th>
                <th className="px-3 py-2.5 font-medium">Drift</th>
                <th className="px-3 py-2.5 font-medium">Conviction</th>
                <th className="px-3 py-2.5 font-medium">Verdict</th>
                <th className="px-3 py-2.5 text-right font-medium">P(win) / EV</th>
                <th className="px-3 py-2.5 text-right font-medium">Close</th>
                <th className="px-3 py-2.5 text-right font-medium">Entry</th>
                <th className="px-3 py-2.5 text-right font-medium">Stop</th>
                <th className="px-3 py-2.5 text-right font-medium">Target</th>
                <th className="px-3 py-2.5 text-right font-medium">Net R:R</th>
                <th className="px-3 py-2.5 text-right font-medium">Risk size</th>
                <th className="px-3 py-2.5 text-right font-medium">20D turnover</th>
                <th className="px-3 py-2.5 text-right font-medium">RSI</th>
                <th className="w-10 px-3 py-2.5" />
              </tr>
            </thead>
            <tbody>
              {items.map((item) => {
                const sizing = computeRiskSizing(item);
                const closePrice =
                  item.metrics && Number.isFinite(item.metrics.closePrice) && item.metrics.closePrice > 0
                    ? item.metrics.closePrice
                    : null;
                const changePct =
                  closePrice != null &&
                  typeof item.metrics?.changePct === 'number' &&
                  Number.isFinite(item.metrics.changePct)
                    ? item.metrics.changePct
                    : null;

                return (
                <tr
                  key={item.ticker}
                  tabIndex={0}
                  onClick={() => onSelect(item.ticker)}
                  onKeyDown={(event) => {
                    if (event.key === 'Enter' || event.key === ' ') {
                      event.preventDefault();
                      onSelect(item.ticker);
                    }
                  }}
                  onPointerEnter={() => preloadTickerDetailDialog(item.ticker, horizon)}
                  onFocus={() => preloadTickerDetailDialog(item.ticker, horizon)}
                  className="cursor-pointer border-t border-line/60 transition hover:bg-ink-800/60 focus-visible:bg-ink-800/60"
                >
                  <td className="sticky-col px-3 py-3.5 align-middle">
                    <span
                      aria-hidden
                      className={cn(
                        'absolute left-0 top-1/2 h-8 w-0.5 -translate-y-1/2 rounded-full',
                        driftAccent[item.driftStatus],
                      )}
                    />
                    <span className="tabular pl-1 text-sm font-semibold text-fg-muted">
                      {String(item.rank).padStart(2, '0')}
                    </span>
                  </td>
                  <td className="sticky-col-2 px-3 py-3.5">
                    <div className="flex items-center gap-2">
                      <button
                        type="button"
                        onClick={(event) => {
                          event.stopPropagation();
                          onSelect(item.ticker);
                        }}
                        className="font-mono type-lead font-semibold text-fg transition hover:text-mint-400"
                      >
                        {item.ticker}
                      </button>
                      <span className="type-badge rounded border border-line/70 px-1.5 py-0.5 text-fg-subtle">
                        {item.sector}
                      </span>
                    </div>
                    <p className="type-caption mt-1 max-w-[16rem] truncate text-fg-subtle">
                      {item.companyName}
                    </p>
                    {item.trapFlags && item.trapFlags.length > 0 ? (
                      <Tooltip
                        label={
                          <span>
                            Disclosure classifier flags: {item.trapFlags.join(', ')}. Hard traps are filtered out
                            server-side; these are the remaining warnings.
                          </span>
                        }
                      >
                        <span
                          tabIndex={0}
                          className="type-badge mt-1.5 inline-flex items-center gap-1 rounded border border-amber-400/40 bg-amber-400/10 px-1.5 py-0.5 text-amber-400"
                        >
                          {item.trapFlags.length} flag{item.trapFlags.length === 1 ? '' : 's'}
                        </span>
                      </Tooltip>
                    ) : null}
                  </td>
                  <td className="px-3 py-3.5">
                    <DriftBadge status={item.driftStatus} previousRank={item.previousRank} />
                  </td>
                  <td className="px-3 py-3.5">
                    <ConvictionBar score={item.convictionScore} />
                  </td>
                  <td className="px-3 py-3.5">
                    <Tooltip label={verdictDescription(item.edgeVerdict)}>
                      <span tabIndex={0}>
                        <VerdictBadge verdict={item.edgeVerdict} />
                      </span>
                    </Tooltip>
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption">
                    <span
                      className={cn(
                        'font-semibold',
                        item.winProbability >= item.breakEvenWinRate ? 'text-mint-400' : 'text-rose-400',
                      )}
                    >
                      {formatProbability(item.winProbability)}
                    </span>
                    <span className="text-fg-subtle"> vs {formatProbability(item.breakEvenWinRate)}</span>
                    <p
                      className={cn(
                        'type-caption mt-1',
                        item.expectedValueR > 0 ? 'text-fg-muted' : 'text-rose-400/80',
                      )}
                    >
                      {formatR(item.expectedValueR)} EV
                    </p>
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption">
                    <span className={closePrice != null ? 'font-semibold text-fg' : 'text-fg-subtle'}>
                      {formatPrice(closePrice)}
                    </span>
                    {changePct != null ? (
                      <p className={cn('type-caption mt-1', describePriceDeltaTone(changePct))}>
                        {formatDeltaPercent(changePct, 2)}
                      </p>
                    ) : null}
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption text-fg">
                    {formatPrice(item.entryPrice)}
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption text-rose-400/90">
                    {formatPrice(item.stopLoss)}
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption text-mint-400">
                    {formatPrice(item.targetPrice)}
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption text-fg-muted">
                    <Tooltip label={`Fee-adjusted reward-to-risk. Gross is ${formatRatio(item.riskRewardRatio)}.`}>
                      <span tabIndex={0}>{formatRatio(item.netRiskRewardRatio)}</span>
                    </Tooltip>
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption text-fg-muted">
                    <Tooltip
                      label={
                        sizing.serverSized
                          ? `Server ticket: ${sizing.shares} shares (${sizing.lots} lot${
                              sizing.lots === 1 ? '' : 's'
                            }) · ${formatInteger(sizing.buyOutlayPhp)} all-in incl. the buy leg · ${formatInteger(
                              sizing.netRiskPhp,
                            )} net risk after both fees · Quarter-Kelly ${sizing.kellyPct.toFixed(1)}% of equity.`
                          : `Quarter-Kelly sizes this plan at ${sizing.kellyPct.toFixed(1)}% of the model budget; ${formatInteger(
                              sizing.boardLot,
                            )}-share board lot.`
                      }
                    >
                      <span tabIndex={0} className={sizing.shares > 0 ? 'text-fg' : 'text-fg-subtle'}>
                        {sizing.shares > 0 ? `${formatInteger(sizing.shares)} sh` : sizing.actionable ? 'no lot' : 'pass'}
                      </span>
                    </Tooltip>
                    <p className="type-caption mt-1 text-fg-subtle">
                      {sizing.shares > 0
                        ? sizing.serverSized
                          ? `${formatCompactPeso(sizing.buyOutlayPhp)} all-in`
                          : `${formatInteger(sizing.lots)} lot${sizing.lots === 1 ? '' : 's'}`
                        : `${sizing.kellyPct.toFixed(1)}% Kelly`}
                    </p>
                  </td>
                  <td className="tabular px-3 py-3.5 text-right type-caption text-fg-muted">
                    {item.metrics ? formatCompactPeso(item.metrics.avgTurnover20dPhp) : '—'}
                  </td>
                  <td
                    className={cn(
                      'tabular px-3 py-3.5 text-right type-caption',
                      rsiTone(item.metrics?.rsi14),
                    )}
                  >
                    {item.metrics ? item.metrics.rsi14.toFixed(1) : '—'}
                  </td>
                  <td className="px-3 py-3.5 text-right">
                    <Tooltip label={item.thesis}>
                      <button
                        type="button"
                        aria-label={`Read the thesis for ${item.ticker}`}
                        onClick={(event) => event.stopPropagation()}
                        className="tap type-caption inline-flex size-7 items-center justify-center rounded-full border border-line/80 font-semibold text-fg-muted transition hover:border-mint-400/50 hover:text-mint-400"
                      >
                        i
                      </button>
                    </Tooltip>
                    <button
                      type="button"
                      disabled={sizing.shares <= 0}
                      onClick={(event) => {
                        event.stopPropagation();
                        onTakeTrade(item);
                      }}
                      className="tap type-caption mt-1.5 block w-full rounded border border-mint-400/40 px-1 py-1 font-semibold text-mint-400 transition hover:bg-mint-400/10 disabled:cursor-not-allowed disabled:border-line/60 disabled:text-fg-faint"
                    >
                      Take
                    </button>
                  </td>
                </tr>
                );
              })}
            </tbody>
          </table>
          </div>
        </>
      )}

      <div className="type-caption flex flex-wrap items-center justify-between gap-x-4 gap-y-1 border-t border-line/60 px-4 py-3 text-fg-subtle sm:px-5">
        <span className="tabular">
          {tradeCount} trade · {smallSizeCount} small size · {watchOnlyCount} watch only ·{' '}
          {recommendedCashPct != null ? `${recommendedCashPct.toFixed(0)}% cash recommended` : `${fundedPicks} funded`}
        </span>
        <span className="measure">Open any name for the odds, pre-mortem, full technicals, disclosures and rank history.</span>
      </div>
    </section>
  );
}
