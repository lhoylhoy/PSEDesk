import { useEffect, useState } from 'react';
import { signalsApi } from '../api/signalsApi';
import type { BacktestResult, Horizon } from '../types/signals';
import { cn } from '../lib/cn';
import { formatDate, formatInteger, formatPercent, formatPrice, formatRatio } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import { describeAlphaPct, describeBacktestExit, describeFriendlyError } from '../lib/plainAdvice';
import { fetchQuery, getSnapshot, queryKeys, queryStaleTime } from '../lib/queryCache';
import { formatProbability, formatR } from '../lib/riskSizing';
import type { ViewMode } from '../lib/viewMode';

interface BacktestPanelProps {
  horizon: Horizon;
  /** Simple mode renames the replay and translates every exit code. */
  viewMode?: ViewMode;
}

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

function Curve({ result }: { result: BacktestResult }) {
  const points = result.equityCurve;

  if (points.length < 2) {
    return <p className="text-xs text-fg-subtle">Not enough settled sessions for a curve.</p>;
  }

  // The index benchmark is drawn on the desk curve's own scale, so the vertical gap between the
  // two lines *is* the alpha. A second axis would flatter or hide it.
  const benchmark = points.map((point) =>
    typeof point.benchmarkEquityPhp === 'number' ? point.benchmarkEquityPhp : null,
  );
  const hasBenchmark = benchmark.some((value) => value !== null);
  const values = [
    ...points.map((point) => point.equityPhp),
    ...benchmark.filter((value): value is number => value !== null),
  ];
  const min = Math.min(...values);
  const max = Math.max(...values);
  const span = max - min || 1;

  const toPath = (series: (number | null)[]) =>
    series
      .map((value, index) => {
        if (value === null) {
          return null;
        }

        const x = (index / (series.length - 1)) * 100;
        const y = 100 - ((value - min) / span) * 100;
        return `${x.toFixed(2)},${y.toFixed(2)}`;
      })
      .filter((segment): segment is string => segment !== null)
      .join(' ');

  return (
    <div>
      <svg viewBox="0 0 100 100" preserveAspectRatio="none" className="h-28 w-full">
        {hasBenchmark ? (
          <polyline
            points={toPath(benchmark)}
            fill="none"
            stroke="currentColor"
            strokeWidth="1.2"
            strokeDasharray="2 2"
            vectorEffect="non-scaling-stroke"
            className="text-fg-subtle"
          />
        ) : null}
        <polyline
          points={toPath(points.map((point) => point.equityPhp))}
          fill="none"
          stroke="currentColor"
          strokeWidth="1.5"
          vectorEffect="non-scaling-stroke"
          className={result.netReturnPct >= 0 ? 'text-mint-400' : 'text-rose-400'}
        />
      </svg>
      <div className="mt-1 flex justify-between text-2xs text-fg-subtle">
        <span>{formatDate(result.startDate)}</span>
        <span className="tabular">
          {formatPrice(min)} – {formatPrice(max)}
        </span>
        <span>{formatDate(result.endDate)}</span>
      </div>
      {hasBenchmark ? (
        <p className="mt-1 flex flex-wrap items-center gap-3 text-2xs text-fg-subtle">
          <span className="inline-flex items-center gap-1">
            <span
              className={cn(
                'inline-block h-0.5 w-4',
                result.netReturnPct >= 0 ? 'bg-mint-400' : 'bg-rose-400',
              )}
            />
            The rules
          </span>
          <span className="inline-flex items-center gap-1">
            <span className="inline-block h-0.5 w-4 border-t border-dashed border-slate-500" />
            PSEi buy &amp; hold
          </span>
        </p>
      ) : null}
    </div>
  );
}


/**
 * Causal walk-forward replay: the horizon's own entry, stop and time-stop rules replayed on
 * the real tape with a five-position book, the portfolio heat and sector caps, the full
 * round-trip fee and a board-lot slippage estimate. A 503 from the API is shown verbatim — the
 * desk never fabricates a curve.
 */
export function BacktestPanel({ horizon, viewMode = 'simple' }: BacktestPanelProps) {
  const simple = viewMode === 'simple';
  const [result, setResult] = useState<BacktestResult | null>(null);
  const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const key = queryKeys.backtest(horizon);
    const cached = getSnapshot<BacktestResult>(key);

    // Switching tabs back and forth must not replay the tape again: a replay the reader
    // already ran paints from memory and only revalidates once its window has expired.
    if (cached) {
      setResult(cached);
      setStatus('ready');
    } else {
      setStatus('loading');
    }

    setError(null);

    const controller = new AbortController();
    const timer = window.setTimeout(() => {
      fetchQuery({
        key,
        staleTimeMs: queryStaleTime.backtest,
        signal: controller.signal,
        fetcher: (signal) => signalsApi.backtest(horizon, undefined, signal),
      })
        .then((next) => {
          if (controller.signal.aborted) {
            return;
          }

          setResult(next);
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
  }, [horizon]);

  if (status === 'loading') {
    return (
      <p className="mt-4 text-xs text-fg-subtle">
        {simple
          ? 'Replaying past prices to see how this plan would have done…'
          : `Replaying ${horizonMeta[horizon].label} rules on the tape…`}
      </p>
    );
  }

  if (status === 'error' && !result) {
    return (
      <p className="mt-4 rounded-lg border border-rose-400/30 bg-rose-400/5 px-3 py-2 text-xs text-rose-400">
        {error ?? 'Could not replay the horizon.'}
      </p>
    );
  }

  if (!result) {
    return null;
  }

  return (
    <div className="mt-4">
      {status === 'error' ? (
        <p className="mb-3 rounded-lg border border-amber-400/30 bg-amber-400/5 px-3 py-2 text-xs text-amber-400">
          Showing the last replay from memory — the refresh failed: {error ?? 'unknown error'}
        </p>
      ) : null}
      <p className="max-w-3xl type-body measure text-fg-muted">
        {simple
          ? `A simulated run on real past prices — not a real account. Up to five trades at once, about 1% of the practice money risked per trade inside a 6% total risk limit, at most two names and 35% of equity per sector, board lots, the full 0.690% round-trip broker fee and a realistic fill cost. It is then compared with just buying the whole market (the PSEi) and holding it. Nothing here looks into the future: every entry is decided only on prices that came before it.`
          : 'Up to five concurrent positions, 1% of equity risked at the stop inside a 6% portfolio heat cap, at most two names and 35% of equity per sector, fractional-Kelly sizing, board-lot rounded, charged the 0.690% statutory round trip plus a board-lot slippage estimate, with the money-manager weights refitted every 60 sessions, and measured against holding PSEI.PS over the identical window. Alpha is the slippage-adjusted net return minus the index — the desk pays friction, the benchmark does not. Every entry is decided on the bars that preceded it: no look-ahead, no re-optimised parameters, no synthetic fill.'}
      </p>
      <p className="mt-2 max-w-3xl rounded-lg border border-line/70 bg-ink-850/60 px-3 py-2 type-caption text-fg-subtle">
        These are hypothetical results from one simulated path. They are not the record of a real
        account, they exclude taxes and your broker’s own pricing, and a favourable past path does not
        repeat. Past or simulated performance is not a reliable indicator of future results.
      </p>

      <dl className="mt-3 grid grid-cols-2 gap-2 xs:grid-cols-3 lg:grid-cols-6">
        <Tile
          label={simple ? 'Total result' : 'Net return'}
          value={formatPercent(result.netReturnPct, 2)}
          detail={`${formatInteger(result.sessionsEvaluated)} ${simple ? 'trading days' : 'sessions'}`}
          tone={result.netReturnPct >= 0 ? 'text-mint-400' : 'text-rose-400'}
        />
        <Tile
          label={simple ? 'Worst dip' : 'Max drawdown'}
          value={formatPercent(-Math.abs(result.maxDrawdownPct), 2)}
          detail={simple ? 'from the highest point' : 'peak to trough'}
          tone="text-rose-400"
        />
        <Tile
          label={simple ? 'Trades taken' : 'Trades'}
          value={formatInteger(result.totalTrades)}
          detail={`${formatProbability(result.winRate)} ${simple ? 'made money' : 'hit rate'}`}
        />
        <Tile
          label={simple ? 'Average per trade' : 'Expectancy'}
          value={formatR(result.expectancyR)}
          detail={simple ? 'after fees' : 'per trade, fee-paid'}
          tone={result.expectancyR > 0 ? 'text-mint-400' : 'text-rose-400'}
        />
        <Tile
          label="Profit factor"
          value={result.profitFactor.toFixed(2)}
          detail={simple ? 'wins vs losses · above 1.00 is good' : 'gross wins ÷ losses'}
        />
        <Tile
          label={simple ? 'Broker fees paid' : 'Fees paid'}
          value={formatPrice(result.totalFeesPaidPhp)}
          detail={`${formatPrice(result.startingCapitalPhp)} → ${formatPrice(result.endingEquityPhp)}`}
        />
        {typeof result.benchmarkReturnPct === 'number' ? (
          <Tile
            label={simple ? 'Vs the market' : 'Alpha vs PSEi'}
            value={formatPercent(result.excessReturnPct, 2)}
            detail={`${simple ? 'market' : 'PSEi'}: ${formatPercent(
              result.benchmarkReturnPct,
              2,
            )} · desk pays fees, the market does not`}
            tone={(result.excessReturnPct ?? 0) >= 0 ? 'text-mint-400' : 'text-rose-400'}
          />
        ) : null}
        <Tile
          label={simple ? 'Simulated CAGR' : 'CAGR'}
          value={formatPercent(result.cagrPct, 1)}
          detail={simple ? 'annualised · not a forecast' : 'compound annual growth'}
          tone={(result.cagrPct ?? 0) >= 0 ? 'text-mint-400' : 'text-rose-400'}
        />
        <Tile
          label="Sharpe"
          value={
            typeof result.annualizedSharpeRatio === 'number'
              ? result.annualizedSharpeRatio.toFixed(2)
              : '—'
          }
          detail={simple ? 'gain per unit of swing · above 1 is good' : 'annualised, risk-free 0'}
        />
        <Tile
          label="Calmar"
          value={formatRatio(result.calmarRatio)}
          detail={simple ? 'yearly gain vs worst dip' : 'CAGR ÷ max drawdown'}
        />
        <Tile
          label={simple ? 'Fill cost' : 'Slippage'}
          value={formatPrice(result.totalSlippagePhp)}
          detail={`${simple ? 'after slippage' : 'net of slippage'} ${formatPercent(
            result.netReturnAfterSlippagePct,
            2,
          )}`}
        />
      </dl>

      <p className="mt-2 type-caption text-fg-subtle">
        {describeAlphaPct(result.excessReturnPct)}
      </p>

      <div className="mt-3 rounded-xl border border-line/70 bg-ink-850/60 p-3">
        <h3 className="type-eyebrow text-fg-subtle">
          {simple ? 'Your practice money over the test' : 'Equity curve'}
        </h3>
        <div className="mt-2">
          <Curve result={result} />
        </div>
      </div>

      {result.trades.length === 0 ? (
        <p className="mt-3 rounded-xl border border-line/60 bg-ink-850/50 px-4 py-6 text-center text-xs text-fg-subtle">
          {simple
            ? 'The plan never found a trade it liked in this window. A quiet test is an honest result, not a bug.'
            : 'The rules never fired a qualifying entry in this window — a flat replay is an honest result, not a bug.'}
        </p>
      ) : (
        <>
          <ul className="mt-3 grid gap-2 md:hidden">
            {result.trades.slice(-20).map((trade) => (
              <li
                key={`${trade.ticker}-${trade.entryDate}`}
                className="rounded-xl border border-line/70 bg-ink-850/70 p-3"
              >
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p className="font-mono text-sm font-semibold text-fg">{trade.ticker}</p>
                    <p className="tabular text-2xs text-fg-subtle">
                      {formatDate(trade.entryDate)} → {formatDate(trade.exitDate)} · {trade.barsHeld}{' '}
                      {simple ? 'days' : 'bars'}
                    </p>
                  </div>
                  <div className="shrink-0 text-right">
                    <p
                      className={cn(
                        'tabular text-sm font-semibold',
                        trade.netPnlPhp >= 0 ? 'text-mint-400' : 'text-rose-400',
                      )}
                    >
                      {formatPrice(trade.netPnlPhp)}
                    </p>
                    <p className={cn('tabular text-2xs', trade.netR >= 0 ? 'text-mint-400' : 'text-rose-400')}>
                      {formatR(trade.netR)}
                    </p>
                  </div>
                </div>
                <p className="tabular mt-2 text-2xs text-fg-muted">
                  {formatPrice(trade.entryPrice)} → {formatPrice(trade.exitPrice)} ·{' '}
                  {formatInteger(trade.shares)} shares
                </p>
                <p className="mt-1 text-2xs leading-snug text-fg-subtle">
                  {simple ? describeBacktestExit(trade.exitReason) : trade.exitReason.toLowerCase()}
                </p>
              </li>
            ))}
          </ul>
          <div className="mt-3 hidden scroll-x rounded-xl border border-line/60 md:block">
            <table className="w-full min-w-176 border-collapse text-left">
              <thead>
                <tr className="type-eyebrow text-fg-subtle">
                  <th className="sticky-col px-3 py-2 font-medium">{simple ? 'Stock' : 'Name'}</th>
                <th className="px-3 py-2 font-medium">{simple ? 'Bought' : 'Entry'}</th>
                <th className="px-3 py-2 font-medium">{simple ? 'Sold' : 'Exit'}</th>
                <th className="px-3 py-2 text-right font-medium">Shares</th>
                <th className="px-3 py-2 text-right font-medium">{simple ? 'Profit' : 'Net P&L'}</th>
                <th className="px-3 py-2 text-right font-medium">R</th>
                <th className="px-3 py-2 text-right font-medium">{simple ? 'Days' : 'Bars'}</th>
                <th className="px-3 py-2 font-medium">{simple ? 'Why it sold' : 'Why'}</th>
              </tr>
            </thead>
            <tbody>
              {result.trades.slice(-20).map((trade) => (
                <tr key={`${trade.ticker}-${trade.entryDate}`} className="border-t border-line/60">
                  <td className="sticky-col px-3 py-2 font-mono text-xs font-semibold tracking-wide text-fg">
                    {trade.ticker}
                  </td>
                  <td className="tabular px-3 py-2 text-xs text-fg-muted">
                    {formatDate(trade.entryDate)} · {formatPrice(trade.entryPrice)}
                  </td>
                  <td className="tabular px-3 py-2 text-xs text-fg-muted">
                    {formatDate(trade.exitDate)} · {formatPrice(trade.exitPrice)}
                  </td>
                  <td className="tabular px-3 py-2 text-right text-xs text-fg-muted">
                    {formatInteger(trade.shares)}
                  </td>
                  <td
                    className={cn(
                      'tabular px-3 py-2 text-right text-xs font-semibold',
                      trade.netPnlPhp >= 0 ? 'text-mint-400' : 'text-rose-400',
                    )}
                  >
                    {formatPrice(trade.netPnlPhp)}
                  </td>
                  <td
                    className={cn(
                      'tabular px-3 py-2 text-right text-xs',
                      trade.netR >= 0 ? 'text-mint-400' : 'text-rose-400',
                    )}
                  >
                    {formatR(trade.netR)}
                  </td>
                  <td className="tabular px-3 py-2 text-right text-xs text-fg-muted">{trade.barsHeld}</td>
                  <td className="px-3 py-2 text-2xs leading-snug text-fg-subtle">
                    {simple ? describeBacktestExit(trade.exitReason) : trade.exitReason.toLowerCase()}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          </div>
        </>
      )}

      <p className="mt-2 text-2xs text-fg-faint">
        {result.trades.length > 20
          ? simple
            ? `Showing the last 20 of ${result.trades.length} trades. `
            : `Showing the last 20 of ${result.trades.length} round trips. `
          : ''}
        {simple
          ? `Tested from ${formatDate(result.startDate)} to ${formatDate(result.endDate)}. One trade at a time keeps the test honest.`
          : `${horizonMeta[horizon].label} book · ${formatDate(result.startDate)} to ${formatDate(
              result.endDate,
            )} · one position at a time keeps the curve causal.`}
      </p>
    </div>
  );
}

