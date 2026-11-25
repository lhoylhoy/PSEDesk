import type { Horizon, SignalItem } from '../types/signals';
import { cn } from '../lib/cn';
import { formatCompactPeso, formatInteger, formatPrice, formatRatio } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
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
      <span className="tabular text-xs font-semibold text-slate-200">{Math.round(score)}</span>
    </div>
  );
}

function rsiTone(value: number | undefined): string {
  if (value == null) {
    return 'text-slate-500';
  }

  if (value > 72) {
    return 'text-amber-400';
  }

  if (value < 40) {
    return 'text-rose-400';
  }

  return 'text-slate-200';
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
  const openDetails = () => onSelect(item.ticker);

  return (
    <li className="px-3 py-3">
      <div className="flex items-start justify-between gap-2">
        <div className="flex min-w-0 items-start gap-2">
          <span className="tabular mt-0.5 w-6 shrink-0 text-sm font-semibold text-slate-400">
            {String(item.rank).padStart(2, '0')}
          </span>
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-1.5">
              <button
                type="button"
                onClick={openDetails}
                onPointerEnter={() => preloadTickerDetailDialog(item.ticker, horizon)}
                onFocus={() => preloadTickerDetailDialog(item.ticker, horizon)}
                className="font-mono text-sm font-semibold tracking-wide text-white transition hover:text-mint-400"
              >
                {item.ticker}
              </button>
              <span className="rounded border border-line/70 px-1 py-0.5 text-[9px] uppercase tracking-wider text-slate-500">
                {item.sector}
              </span>
              <DriftBadge status={item.driftStatus} previousRank={item.previousRank} />
            </div>
            <p className="mt-0.5 truncate text-[11px] text-slate-500">{item.companyName}</p>
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
                  className="mt-1 inline-flex items-center gap-1 rounded border border-amber-400/40 bg-amber-400/10 px-1 py-0.5 text-[9px] uppercase tracking-wider text-amber-400"
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

      <dl className="mt-2 grid grid-cols-3 gap-1.5">
        <div className="rounded-lg border border-line/60 bg-ink-850/60 px-2 py-1.5">
          <dt className="text-[9px] uppercase tracking-wider text-slate-500">Entry</dt>
          <dd className="tabular text-xs font-semibold text-slate-100">
            {formatPrice(item.entryPrice)}
          </dd>
        </div>
        <div className="rounded-lg border border-line/60 bg-ink-850/60 px-2 py-1.5">
          <dt className="text-[9px] uppercase tracking-wider text-slate-500">Stop</dt>
          <dd className="tabular text-xs font-semibold text-rose-400/90">
            {formatPrice(item.stopLoss)}
          </dd>
        </div>
        <div className="rounded-lg border border-line/60 bg-ink-850/60 px-2 py-1.5">
          <dt className="text-[9px] uppercase tracking-wider text-slate-500">Target</dt>
          <dd className="tabular text-xs font-semibold text-mint-400">
            {formatPrice(item.targetPrice)}
          </dd>
        </div>
      </dl>

      <div className="mt-2 flex flex-wrap items-center justify-between gap-2">
        <p className="tabular text-[10px] text-slate-500">
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
            className="rounded-lg border border-line/80 px-2.5 py-1.5 text-[10px] font-semibold uppercase tracking-wider text-slate-300 transition hover:border-slate-500/60 hover:text-white"
          >
            Details
          </button>
          <button
            type="button"
            disabled={sizing.shares <= 0}
            onClick={() => onTakeTrade(item)}
            className="rounded-lg border border-mint-400/40 px-2.5 py-1.5 text-[10px] font-semibold uppercase tracking-wider text-mint-400 transition hover:bg-mint-400/10 disabled:cursor-not-allowed disabled:border-line/60 disabled:text-slate-600"
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
      <header className="flex flex-wrap items-end justify-between gap-2 border-b border-line/70 px-4 py-3">
        <div>
          <h2 className="text-sm font-semibold text-slate-100">{meta.bookTitle}</h2>
          <p className="mt-0.5 max-w-3xl text-[11px] text-slate-500">{meta.mandate}</p>
        </div>
        <span className="hidden text-[10px] uppercase tracking-[0.18em] text-slate-500 sm:block">
          {items.length} active names · edge-sized plans
        </span>
      </header>

      {items.length === 0 ? (
        <p className="px-4 py-10 text-center text-xs text-slate-500">
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

          <div className="hidden overflow-x-auto md:block">
            <table className="w-full min-w-248 border-collapse text-left">
            <thead>
              <tr className="text-[10px] uppercase tracking-[0.16em] text-slate-500">
                <th className="sticky-col w-12 px-3 py-2.5 font-medium">#</th>
                <th className="sticky-col-2 px-3 py-2.5 font-medium">Name</th>
                <th className="px-3 py-2.5 font-medium">Drift</th>
                <th className="px-3 py-2.5 font-medium">Conviction</th>
                <th className="px-3 py-2.5 font-medium">Verdict</th>
                <th className="px-3 py-2.5 text-right font-medium">P(win) / EV</th>
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
                  <td className="sticky-col px-3 py-3 align-middle">
                    <span
                      aria-hidden
                      className={cn(
                        'absolute left-0 top-1/2 h-8 w-0.5 -translate-y-1/2 rounded-full',
                        driftAccent[item.driftStatus],
                      )}
                    />
                    <span className="tabular pl-1 text-sm font-semibold text-slate-400">
                      {String(item.rank).padStart(2, '0')}
                    </span>
                  </td>
                  <td className="sticky-col-2 px-3 py-3">
                    <div className="flex items-center gap-2">
                      <button
                        type="button"
                        onClick={(event) => {
                          event.stopPropagation();
                          onSelect(item.ticker);
                        }}
                        className="font-mono text-sm font-semibold tracking-wide text-white transition hover:text-mint-400"
                      >
                        {item.ticker}
                      </button>
                      <span className="rounded border border-line/70 px-1 py-0.5 text-[9px] uppercase tracking-wider text-slate-500">
                        {item.sector}
                      </span>
                    </div>
                    <p className="mt-0.5 max-w-[16rem] truncate text-[11px] text-slate-500">
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
                          className="mt-1 inline-flex items-center gap-1 rounded border border-amber-400/40 bg-amber-400/10 px-1 py-0.5 text-[9px] uppercase tracking-wider text-amber-400"
                        >
                          {item.trapFlags.length} flag{item.trapFlags.length === 1 ? '' : 's'}
                        </span>
                      </Tooltip>
                    ) : null}
                  </td>
                  <td className="px-3 py-3">
                    <DriftBadge status={item.driftStatus} previousRank={item.previousRank} />
                  </td>
                  <td className="px-3 py-3">
                    <ConvictionBar score={item.convictionScore} />
                  </td>
                  <td className="px-3 py-3">
                    <Tooltip label={verdictDescription(item.edgeVerdict)}>
                      <span tabIndex={0}>
                        <VerdictBadge verdict={item.edgeVerdict} />
                      </span>
                    </Tooltip>
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs">
                    <span
                      className={cn(
                        'font-semibold',
                        item.winProbability >= item.breakEvenWinRate ? 'text-mint-400' : 'text-rose-400',
                      )}
                    >
                      {formatProbability(item.winProbability)}
                    </span>
                    <span className="text-slate-500"> vs {formatProbability(item.breakEvenWinRate)}</span>
                    <p
                      className={cn(
                        'mt-0.5 text-[10px]',
                        item.expectedValueR > 0 ? 'text-slate-400' : 'text-rose-400/80',
                      )}
                    >
                      {formatR(item.expectedValueR)} EV
                    </p>
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-slate-100">
                    {formatPrice(item.entryPrice)}
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-rose-400/90">
                    {formatPrice(item.stopLoss)}
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-mint-400">
                    {formatPrice(item.targetPrice)}
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-slate-300">
                    <Tooltip label={`Fee-adjusted reward-to-risk. Gross is ${formatRatio(item.riskRewardRatio)}.`}>
                      <span tabIndex={0}>{formatRatio(item.netRiskRewardRatio)}</span>
                    </Tooltip>
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-slate-400">
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
                      <span tabIndex={0} className={sizing.shares > 0 ? 'text-slate-200' : 'text-slate-500'}>
                        {sizing.shares > 0 ? `${formatInteger(sizing.shares)} sh` : sizing.actionable ? 'no lot' : 'pass'}
                      </span>
                    </Tooltip>
                    <p className="mt-0.5 text-[10px] text-slate-600">
                      {sizing.shares > 0
                        ? sizing.serverSized
                          ? `${formatCompactPeso(sizing.buyOutlayPhp)} all-in`
                          : `${formatInteger(sizing.lots)} lot${sizing.lots === 1 ? '' : 's'}`
                        : `${sizing.kellyPct.toFixed(1)}% Kelly`}
                    </p>
                  </td>
                  <td className="tabular px-3 py-3 text-right text-xs text-slate-400">
                    {item.metrics ? formatCompactPeso(item.metrics.avgTurnover20dPhp) : '—'}
                  </td>
                  <td
                    className={cn(
                      'tabular px-3 py-3 text-right text-xs',
                      rsiTone(item.metrics?.rsi14),
                    )}
                  >
                    {item.metrics ? item.metrics.rsi14.toFixed(1) : '—'}
                  </td>
                  <td className="px-3 py-3 text-right">
                    <Tooltip label={item.thesis}>
                      <button
                        type="button"
                        aria-label={`Read the thesis for ${item.ticker}`}
                        onClick={(event) => event.stopPropagation()}
                        className="inline-flex size-6 items-center justify-center rounded-full border border-line/80 text-[10px] font-semibold text-slate-400 transition hover:border-mint-400/50 hover:text-mint-400"
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
                      className="mt-1 block w-full rounded border border-mint-400/40 px-1 py-0.5 text-[9px] font-semibold uppercase tracking-wider text-mint-400 transition hover:bg-mint-400/10 disabled:cursor-not-allowed disabled:border-line/60 disabled:text-slate-600"
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

      <div className="flex flex-wrap items-center justify-between gap-2 border-t border-line/60 px-4 py-2 text-[10px] text-slate-600">
        <span>
          {tradeCount} TRADE · {smallSizeCount} SMALL_SIZE · {watchOnlyCount} WATCH_ONLY ·{' '}
          {recommendedCashPct != null ? `${recommendedCashPct.toFixed(0)}% cash recommended` : `${fundedPicks} funded`}
        </span>
        <span>Open any name for the odds, pre-mortem, full technicals, disclosures and rank history.</span>
      </div>
    </section>
  );
}
