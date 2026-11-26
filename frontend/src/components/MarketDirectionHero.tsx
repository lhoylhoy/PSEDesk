import type { ReactNode } from 'react';
import type { Horizon, MarketDirectionForecast, RegimeSnapshot, SignalRun, TrustPulse } from '../types/signals';
import { cn } from '../lib/cn';
import { formatWeekday } from '../lib/formatters';
import { describeMarketCall, describeMarketMood, describeTrustPulse } from '../lib/plainAdvice';
import { DisclosurePill, TechnicalDisclosure } from './ui/TechnicalDisclosure';
import { IndicatorTiles, RotationStrip } from './RegimeStrip';

interface MarketDirectionHeroProps {
  regime: RegimeSnapshot | null;
  horizon: Horizon;
  run?: SignalRun | null;
  /** The desk's own graded record; null hides the pulse instead of inventing a win rate. */
  trustPulse?: TrustPulse | null;
  onOpenScorecard?: () => void;
  /** Extra calm figures (cash posture, breadth) that belong inside the hero card. */
  footer?: ReactNode;
}

const directionLabels: Record<string, string> = {
  UPTREND: 'Leaning up',
  SIDEWAYS: 'Sideways',
  DOWNTREND: 'Leaning down',
};

/** A 10–90 gauge: the marker sits at the server's score, the reader never computes it. */
function ScoreGauge({ forecast }: { forecast: MarketDirectionForecast }) {
  const score = Math.max(10, Math.min(90, forecast.bullishScore));

  return (
    <div className="w-full rounded-xl border border-line/70 bg-ink-850/80 px-3 py-2.5 sm:max-w-xs">
      <div className="flex items-baseline justify-between gap-2">
        <p className="text-[10px] uppercase tracking-[0.16em] text-slate-500">Direction score</p>
        <p className="tabular text-2xl font-semibold leading-none text-slate-100">
          {score}
          <span className="text-xs font-normal text-slate-500"> / 100</span>
        </p>
      </div>
      <div className="relative mt-3 h-2 rounded-full bg-linear-to-r from-rose-500/60 via-slate-500/40 to-mint-500/70">
        <span
          aria-hidden
          className="absolute top-1/2 h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-ink-900 bg-white shadow"
          style={{ left: `${score}%` }}
        />
      </div>
      <div className="mt-1 flex justify-between text-[9px] uppercase tracking-[0.12em] text-slate-600">
        <span>Bearish</span>
        <span>Neutral</span>
        <span>Bullish</span>
      </div>
      <p className="mt-1 text-[10px] text-slate-500">
        {directionLabels[forecast.direction] ?? 'Unclear'} · {forecast.confidence.toLowerCase()} conviction
      </p>
    </div>
  );
}

function SectorChips({ label, sectors, tone }: { label: string; sectors: string[]; tone: string }) {
  if (sectors.length === 0) {
    return null;
  }

  return (
    <div className="flex flex-wrap items-center gap-1.5">
      <span className="text-[10px] uppercase tracking-[0.14em] text-slate-500">{label}</span>
      {sectors.map((sector) => (
        <span key={sector} className={cn('rounded-full border px-2 py-0.5 text-[10px] font-semibold', tone)}>
          {sector}
        </span>
      ))}
    </div>
  );
}


/**
 * Simple mode's opening answer: one traffic-light call, the 10–90 score behind it, the plain
 * reasons and the sectors money is moving in and out of. Every field is server-computed; the
 * component only formats it. The institutional tiles stay one tap away in the disclosure.
 */
export function MarketDirectionHero({
  regime,
  horizon,
  run,
  trustPulse,
  onOpenScorecard,
  footer,
}: MarketDirectionHeroProps) {
  if (!regime) {
    return (
      <section className="panel p-5">
        <p className="text-[10px] uppercase tracking-[0.24em] text-slate-500">The call</p>
        <p className="mt-2 text-sm text-slate-400">
          No market session saved yet. Run the latest screen to pull today’s PSE prices and company news.
        </p>
      </section>
    );
  }

  const forecast = regime.directionForecast ?? null;
  const fallbackCall =
    regime.regime === 'BEARISH' ? 'STAY_IN_CASH' : regime.regime === 'BULLISH' ? 'RIDE_THE_WAVE' : 'BE_SELECTIVE';
  const call = describeMarketCall(forecast?.call ?? fallbackCall);
  const mood = describeMarketMood(regime.regime);
  const headline = forecast?.headline ?? mood.headline;
  const summary = forecast?.actionSummary ?? mood.explanation;

  return (
    <section className="panel p-4 sm:p-5">
      <p className="text-[10px] uppercase tracking-[0.24em] text-slate-500">
        The call · {formatWeekday(regime.tradeDate)}
      </p>

      <div className="mt-2 flex flex-col gap-4 lg:flex-row lg:items-start lg:justify-between lg:gap-6">
        <div className="min-w-0 max-w-2xl">
          <div className="flex flex-wrap items-center gap-2">
            <span
              className={cn(
                'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[10px] font-semibold uppercase tracking-[0.16em]',
                call.badgeClass,
              )}
            >
              <span aria-hidden className={cn('h-2 w-2 rounded-full', call.dotClass)} />
              {call.label}
            </span>
            <span className="text-[10px] font-semibold text-slate-400">{call.instruction}</span>
          </div>
          {/* Fluid: a 320px phone cannot hold a 24px headline in this column, and a 2560px
              monitor makes a 20px one look like body copy. */}
          <h2 className="text-fluid-hero mt-2 font-semibold text-slate-100">{headline}</h2>
          <p className="mt-1 text-xs leading-relaxed text-slate-400">{summary}</p>
        </div>

        {forecast ? <ScoreGauge forecast={forecast} /> : null}
      </div>

      {forecast && forecast.reasons.length > 0 ? (
        <ul className="mt-4 space-y-1.5 border-t border-line/60 pt-3">
          {forecast.reasons.map((reason) => (
            <li key={reason} className="flex gap-2 text-[11px] leading-relaxed text-slate-400">
              <span aria-hidden className="mt-1.5 h-1 w-1 shrink-0 rounded-full bg-slate-500" />
              <span>{reason}</span>
            </li>
          ))}
        </ul>
      ) : null}

      {forecast && (forecast.leadingSectors.length > 0 || forecast.laggingSectors.length > 0) ? (
        <div className="mt-3 flex flex-col gap-1.5">
          <SectorChips
            label="Money in"
            sectors={forecast.leadingSectors}
            tone="border-mint-400/40 bg-mint-400/10 text-mint-400"
          />
          <SectorChips
            label="Money out"
            sectors={forecast.laggingSectors}
            tone="border-rose-400/40 bg-rose-400/10 text-rose-400"
          />
        </div>
      ) : null}

      {trustPulse ? (
        <div className="mt-4 flex flex-wrap items-center justify-between gap-2 rounded-xl border border-line/70 bg-ink-900/70 px-3 py-2">
          <p className="text-[11px] leading-relaxed text-slate-400">
            <span className="font-semibold text-slate-200">How this desk has done: </span>
            {describeTrustPulse(trustPulse)}
          </p>
          {onOpenScorecard ? (
            <button
              type="button"
              onClick={onOpenScorecard}
              className="tap px-1 text-[10px] font-semibold text-slate-300 underline decoration-line underline-offset-2 transition hover:text-mint-400"
            >
              See the full record
            </button>
          ) : null}
        </div>
      ) : null}

      {footer ? <div className="mt-4 border-t border-line/60 pt-3">{footer}</div> : null}

      <TechnicalDisclosure
        className="mt-4"
        title="Show the numbers behind the call"
        summary={
          <>
            <DisclosurePill>PSEi {regime.pseiClose.toLocaleString('en-PH', { maximumFractionDigits: 2 })}</DisclosurePill>
            <DisclosurePill>{horizon.toUpperCase()}</DisclosurePill>
          </>
        }
      >
        <IndicatorTiles regime={regime} run={run} horizon={horizon} />
        <RotationStrip rotation={regime.sectorRotation ?? []} />
      </TechnicalDisclosure>
    </section>
  );
}
