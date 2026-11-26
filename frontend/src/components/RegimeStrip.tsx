import type { Horizon, RegimeSnapshot, SectorQuadrant, SectorRotation, SignalRun, TrustPulse } from '../types/signals';
import { cn } from '../lib/cn';
import { formatCompactPeso, formatDeltaBasisPoints, formatDeltaPercent, formatPercent, formatWeekday } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import { describeCashAdvice } from '../lib/plainAdvice';
import type { ViewMode } from '../lib/viewMode';
import { MarketDirectionHero } from './MarketDirectionHero';

const regimeStyles = {
  BULLISH: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
  NEUTRAL: 'border-slate-500/40 bg-slate-500/10 text-fg-muted',
  BEARISH: 'border-rose-400/40 bg-rose-400/10 text-rose-400',
} as const;

/** Quadrant palette: money flows in from LEADING down to LAGGING. */
const quadrantStyles: Record<SectorQuadrant, string> = {
  LEADING: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
  IMPROVING: 'border-sky-400/40 bg-sky-400/10 text-sky-300',
  WEAKENING: 'border-amber-400/40 bg-amber-400/10 text-amber-300',
  LAGGING: 'border-rose-400/40 bg-rose-400/10 text-rose-400',
};

interface RegimeStripProps {
  regime: RegimeSnapshot | null;
  horizon: Horizon;
  /** Current run, used for the breadth and cash-posture tiles. */
  run?: SignalRun | null;
  /** Simple mode leads with the direction call; Pro shows the institutional tiles upfront. */
  viewMode?: ViewMode;
  /** The desk's graded record, shown as a quiet trust line under the call. */
  trustPulse?: TrustPulse | null;
  onOpenScorecard?: () => void;
}

function MetricTile({ label, value, detail, tone }: { label: string; value: string; detail?: string; tone?: string }) {
  return (
    <div className="rounded-xl border border-line/70 bg-ink-850/80 px-3.5 py-3">
      <dt className="type-eyebrow text-fg-subtle">{label}</dt>
      <dd className={cn('tabular type-figure mt-1.5 text-base text-fg', tone)}>{value}</dd>
      {detail ? <p className="type-caption mt-1 text-fg-subtle">{detail}</p> : null}
    </div>
  );
}
/** Larger card used for the three Simple-mode summary figures. */
function SummaryCard({
  label,
  value,
  detail,
  tone,
}: {
  label: string;
  value: string;
  detail: string;
  tone?: string;
}) {
  return (
    <div className="rounded-xl border border-line/70 bg-ink-850/80 px-3.5 py-3">
      <dt className="type-eyebrow text-fg-subtle">{label}</dt>
      <dd className={cn('tabular text-fluid-metric type-figure mt-1.5 text-fg', tone)}>{value}</dd>
      <p className="type-caption measure mt-1 text-fg-subtle">{detail}</p>
    </div>
  );
}



function RotationCard({ card }: { card: SectorRotation }) {
  const quadrant = (card.quadrant as SectorQuadrant) ?? 'LAGGING';

  return (
    <li className="rounded-xl border border-line/70 bg-ink-850/80 px-3.5 py-3">
      <div className="flex items-baseline justify-between gap-2">
        <p className="type-body truncate font-semibold text-fg" title={card.sector}>
          {card.sector}
        </p>
        <span
          className={cn(
            'type-badge shrink-0 rounded-full border px-2 py-0.5',
            quadrantStyles[quadrant] ?? quadrantStyles.LAGGING,
          )}
        >
          {card.quadrant}
        </span>
      </div>
      <p className="tabular type-caption mt-1.5 text-fg-muted">
        {card.compositeScore.toFixed(0)} score · {card.stockCount} name{card.stockCount === 1 ? '' : 's'}
      </p>
      <p className="tabular type-caption mt-1 text-fg-subtle">
        RS {formatDeltaPercent(card.avgRelativeStrength63d, 1)} · {formatPercent(card.aboveSma50Pct, 0, false)} &gt; 50D
      </p>
    </li>
  );
}

export function RotationStrip({ rotation }: { rotation: SectorRotation[] }) {
  if (rotation.length === 0) {
    return null;
  }

  return (
    <div className="mt-4 border-t border-line/60 pt-3.5">
      <p className="type-eyebrow text-fg-subtle">
        Sector rotation · best composite first
      </p>
      <ul className="mt-2.5 grid grid-cols-1 gap-2.5 sm:grid-cols-2 lg:grid-cols-4 2xl:grid-cols-5 3xl:grid-cols-6">
        {rotation.map((card) => (
          <RotationCard key={card.sector} card={card} />
        ))}
      </ul>
    </div>
  );
}

const dash = '—';

/** Provenance tag from the server, rendered in plain language for the tape strip. */
function describeForeignSource(source: string | null): string {
  switch (source) {
    case 'pse-dqr-live':
      return 'official PSE DQR · fetched live';
    case 'pse-dqr-upload':
      return 'official PSE DQR · uploaded';
    default:
      return "today's flow";
  }
}

/** Green for a risk-on move, red for risk-off, muted when the feed never reported. */
function changeTone(value: number | null, positiveIsBullish = true): string {
  if (value === null || !Number.isFinite(value) || value === 0) {
    return 'text-fg-subtle';
  }

  const bullish = value > 0;
  return bullish === positiveIsBullish ? 'text-mint-400' : 'text-rose-400';
}

/** The nine institutional tape tiles. Kept as its own component so Simple mode can nest the identical markup. */
export function IndicatorTiles({
  regime,
  run,
  horizon,
}: {
  regime: RegimeSnapshot;
  run?: SignalRun | null;
  horizon: Horizon;
}) {
  const foreignReported = regime.netForeignFlowPhp !== null;
  const foreignPositive = foreignReported && regime.netForeignFlowPhp! >= 0;

  // Newer snapshots carry breadth on the snapshot itself; older ones only have it on the run.
  const breadthAbove50 = regime.breadthAboveSma50Pct ?? run?.breadth?.aboveSma50Pct ?? null;
  const breadthAbove200 = regime.breadthAboveSma200Pct ?? run?.breadth?.aboveSma200Pct ?? null;
  const stage2Count = regime.stage2Count ?? run?.breadth?.stage2Count ?? null;
  const evaluatedCount = regime.evaluatedStockCount ?? run?.breadth?.evaluatedStockCount ?? null;
  const breadthReported = breadthAbove50 !== null;
  const breadthHealthy = breadthReported && breadthAbove50 >= 50;

  return (
    <dl className="grid w-full grid-cols-2 gap-2 xs:grid-cols-3 xl:grid-cols-5 3xl:grid-cols-9">
      <MetricTile
        label="PSEi close"
        value={regime.pseiClose.toLocaleString('en-PH', { maximumFractionDigits: 2 })}
        detail={`50D ${regime.pseiSma50.toLocaleString('en-PH', { maximumFractionDigits: 0 })} · 200D ${regime.pseiSma200.toLocaleString('en-PH', { maximumFractionDigits: 0 })}`}
      />
      <MetricTile
        label="Net foreign"
        value={formatCompactPeso(regime.netForeignFlowPhp)}
        detail={foreignReported ? describeForeignSource(regime.foreignFlowSource) : 'unreported by feed'}
        tone={foreignReported ? (foreignPositive ? 'text-mint-400' : 'text-rose-400') : 'text-fg-subtle'}
      />
      <MetricTile
        label="USD/PHP"
        value={regime.usdPhp.toFixed(4)}
        detail={
          regime.usdPhpSma50 !== null
            ? `50D ${regime.usdPhpSma50.toFixed(4)} · 20D ${formatDeltaPercent(regime.usdPhpChange20dPct, 1)}`
            : 'Yahoo Finance PHP=X close'
        }
        tone={
          regime.usdPhpChange20dPct !== null
            ? regime.usdPhpChange20dPct > 0
              ? 'text-amber-300'
              : 'text-mint-400'
            : undefined
        }
      />
      <MetricTile
        label="US 10Y"
        value={regime.us10YClosePct !== null ? `${regime.us10YClosePct.toFixed(3)}%` : dash}
        detail={
          regime.us10YChange20dBps !== null
            ? `20D ${formatDeltaBasisPoints(regime.us10YChange20dBps)} · ^TNX`
            : 'unreported by feed'
        }
        tone={changeTone(regime.us10YChange20dBps, false)}
      />
      <MetricTile
        label="Brent"
        value={regime.brentUsd !== null ? `$${regime.brentUsd.toFixed(2)}` : dash}
        detail={
          regime.brentChange20dPct !== null
            ? `20D ${formatDeltaPercent(regime.brentChange20dPct, 1)} · BZ=F`
            : 'unreported by feed'
        }
        tone={changeTone(regime.brentChange20dPct, false)}
      />
      <MetricTile
        label="EPHE"
        value={regime.epheCloseUsd !== null ? `$${regime.epheCloseUsd.toFixed(2)}` : dash}
        detail={
          regime.epheChange20dPct !== null
            ? `20D ${formatDeltaPercent(regime.epheChange20dPct, 1)} · MSCI PH ETF`
            : 'unreported by feed'
        }
        tone={changeTone(regime.epheChange20dPct)}
      />
      <MetricTile
        label="PSEi vol 20D"
        value={formatPercent(regime.pseiRealizedVol20dPct, 1, false)}
        detail="annualised, realized"
        tone={
          regime.pseiRealizedVol20dPct !== null && regime.pseiRealizedVol20dPct >= 25
            ? 'text-rose-400'
            : undefined
        }
      />
      <MetricTile
        label="Breadth 50D"
        value={breadthReported ? `${breadthAbove50.toFixed(0)}%` : '—'}
        detail={
          breadthAbove200 !== null
            ? `${breadthAbove200.toFixed(0)}% above 200D · ${stage2Count ?? 0} Stage 2 of ${evaluatedCount ?? 0}`
            : 'breadth unreported'
        }
        tone={breadthReported ? (breadthHealthy ? 'text-mint-400' : 'text-amber-400') : 'text-fg-subtle'}
      />
      <MetricTile
        label="Cash posture"
        value={run ? `${run.recommendedCashPct.toFixed(0)}%` : '—'}
        detail={
          run
            ? `${run.tradeCount} trade · ${run.smallSizeCount} small · ${run.watchOnlyCount} watch`
            : 'run the pipeline'
        }
      />
      <MetricTile
        label="Desk stance"
        value={horizonMeta[horizon].stance}
        detail={`long-only, 10 names · ${horizonMeta[horizon].holding}`}
      />
    </dl>
  );
}


export function RegimeStrip({ regime, horizon, run, viewMode = 'simple', trustPulse, onOpenScorecard }: RegimeStripProps) {
  const simple = viewMode === 'simple';

  if (simple) {
    const buyCount = run ? run.tradeCount + run.smallSizeCount : null;

    // Simple mode opens with the direction call; the three calm figures ride along inside the
    // same card so the home screen reads top-to-bottom without a wall of tiles.
    return (
      <MarketDirectionHero
        regime={regime}
        horizon={horizon}
        run={run}
        trustPulse={trustPulse}
        onOpenScorecard={onOpenScorecard}
        footer={
          regime ? (
            <dl className="grid grid-cols-1 gap-2 sm:grid-cols-3">
              <SummaryCard
                label="Cash to keep safe"
                value={run ? `${run.recommendedCashPct.toFixed(0)}%` : dash}
                detail={describeCashAdvice(run?.recommendedCashPct)}
              />
              <SummaryCard
                label="Names that qualify"
                value={buyCount !== null ? String(buyCount) : dash}
                detail={
                  run
                    ? `${run.tradeCount} full size · ${run.smallSizeCount} half size · ${run.watchOnlyCount} watch only`
                    : 'Run the latest screen to fill this in'
                }
                tone={buyCount ? 'text-mint-400' : undefined}
              />
              <SummaryCard
                label="PSEi & Peso rate"
                value={regime.pseiClose.toLocaleString('en-PH', { maximumFractionDigits: 2 })}
                detail={`USD/PHP ${regime.usdPhp.toFixed(4)} · PSEi ${
                  regime.sma50Status === 'ABOVE' ? 'above' : 'below'
                } its 50-day average`}
              />
            </dl>
          ) : null
        }
      />
    );
  }

  if (!regime) {
    return (
      <section className="panel p-4 sm:p-5">
        <p className="type-eyebrow text-fg-subtle">Tape · {horizon}</p>
        <p className="type-body measure mt-2 text-fg-muted">
          No live PSE session stored yet. Run the pipeline to pull Yahoo Finance candles and PSE Edge disclosures.
        </p>
      </section>
    );
  }

  return (
    <section className="panel p-4 sm:p-5">
      <div className="flex flex-col gap-5 lg:flex-row lg:items-start lg:justify-between">
        <div className="max-w-2xl">
          <p className="type-eyebrow text-fg-subtle">
            Tape · {horizon} horizon · {formatWeekday(regime.tradeDate)}
          </p>
          <div className="mt-2.5 flex flex-wrap items-center gap-2">
            <span
              className={cn(
                'type-badge rounded-full border px-2.5 py-1',
                regimeStyles[regime.regime],
              )}
            >
              {regime.regime}
            </span>
            <span className="type-caption text-fg-muted">
              PSEi {regime.sma50Status === 'ABOVE' ? 'above' : 'below'} 50D and{' '}
              {regime.sma200Status === 'ABOVE' ? 'above' : 'below'} 200D
            </span>
          </div>
          <p className="type-body measure mt-3 text-fg-muted">{regime.regimeNote}</p>
        </div>

        <IndicatorTiles regime={regime} run={run} horizon={horizon} />
      </div>
      {regime.sectorRotation ? <RotationStrip rotation={regime.sectorRotation} /> : null}
    </section>
  );
}
