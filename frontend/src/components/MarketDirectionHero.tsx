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
    <div className="w-full rounded-xl border border-line/70 bg-ink-850/80 px-3.5 py-3 sm:max-w-xs">
      <div className="flex items-baseline justify-between gap-2">
        <p className="type-eyebrow text-fg-subtle">Direction score</p>
        <p className="type-figure text-2xl text-fg">
          {score}
          <span className="text-xs font-normal text-fg-subtle"> / 100</span>
        </p>
      </div>
      <div className="relative mt-3 h-2 rounded-full bg-linear-to-r from-rose-500/60 via-slate-500/40 to-mint-500/70">
        <span
          aria-hidden
          className="absolute top-1/2 h-3.5 w-3.5 -translate-x-1/2 -translate-y-1/2 rounded-full border-2 border-ink-900 bg-white shadow"
          style={{ left: `${score}%` }}
        />
      </div>
      <div className="mt-1.5 flex justify-between type-eyebrow text-fg-faint">
        <span>Bearish</span>
        <span>Neutral</span>
        <span>Bullish</span>
      </div>
      <p className="mt-1 type-caption text-fg-subtle">
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
      <span className="type-eyebrow text-fg-subtle">{label}</span>
      {sectors.map((sector) => (
        <span key={sector} className={cn('rounded-full border px-2 py-0.5 type-caption font-semibold', tone)}>
          {sector}
        </span>
      ))}
    </div>
  );
}


/**
 * Simple mode's call bar: one traffic light, one instruction and one sentence.
 *
 * The 10–90 score, the plain reasons, the sectors money is moving in and out of, the desk's own
 * record and the institutional tiles are all behind the `Why this call` drawer. Every field is
 * server-computed; the component only formats it.
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
        <p className="type-eyebrow text-fg-subtle">The call</p>
        <p className="type-body measure mt-2 text-fg-muted">
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
  const score = forecast ? Math.max(10, Math.min(90, forecast.bullishScore)) : null;

  return (
    /*
     * One bar, not a hero. A first-time reader opens this screen to see the signals; the call
     * is the context for the list below, so it keeps the traffic light, the instruction and one
     * sentence. The gauge, the reasons, the sector money flows, the desk's record and the nine
     * institutional dials are all still here — one tap down, unchanged.
     */
    <section className="panel px-4 py-3.5 sm:px-5 sm:py-4">
      <div className="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
        <p className="type-eyebrow text-fg-subtle simple-label">
          The call · {formatWeekday(regime.tradeDate)}
        </p>
        {score !== null ? (
          <p className="tabular type-caption text-fg-subtle simple-note">Direction score {score} / 100</p>
        ) : null}
      </div>

      <div className="mt-2 flex flex-wrap items-center gap-x-2 gap-y-1">
        <span
          className={cn(
            'inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 type-badge',
            call.badgeClass,
          )}
        >
          <span aria-hidden className={cn('h-2 w-2 rounded-full', call.dotClass)} />
          {call.label}
        </span>
        <span className="type-caption font-semibold text-fg-muted simple-label">{call.instruction}</span>
      </div>

      {/*
       * The focal sentence of the Simple screen, and the only place a display size is
       * earned: a first-time reader reads this line before any row. It is fluid (17→24px)
       * and capped at a readable measure so a long call breaks into three lines, never one
       * 120-character ribbon.
       */}
      <h2 className="text-fluid-hero measure-wide mt-2 font-semibold text-fg">{headline}</h2>

      <TechnicalDisclosure
        className="mt-3"
        title="Why this call"
        summary={
          <>
            <DisclosurePill>PSEi {regime.pseiClose.toLocaleString('en-PH', { maximumFractionDigits: 2 })}</DisclosurePill>
            {run ? <DisclosurePill>Cash {run.recommendedCashPct.toFixed(0)}%</DisclosurePill> : null}
            <DisclosurePill>{horizon.toUpperCase()}</DisclosurePill>
          </>
        }
      >
        <p className="type-body measure text-fg-muted simple-copy">{summary}</p>

        {forecast ? (
          <div className="mt-4">
            <ScoreGauge forecast={forecast} />
          </div>
        ) : null}

        {forecast && forecast.reasons.length > 0 ? (
          <ul className="mt-4 space-y-2 border-t border-line/60 pt-4">
            {forecast.reasons.map((reason) => (
              <li key={reason} className="flex gap-2.5 type-caption text-fg-muted simple-note">
                <span aria-hidden className="mt-2 h-1 w-1 shrink-0 rounded-full bg-slate-500" />
                <span className="measure">{reason}</span>
              </li>
            ))}
          </ul>
        ) : null}

        {forecast && (forecast.leadingSectors.length > 0 || forecast.laggingSectors.length > 0) ? (
          <div className="mt-4 flex flex-col gap-2">
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
          <div className="mt-4 flex flex-wrap items-center justify-between gap-3 rounded-xl border border-line/70 bg-ink-900/70 px-3.5 py-3">
            <p className="type-caption measure text-fg-muted simple-note">
              <span className="font-semibold text-fg">How this desk has done: </span>
              {describeTrustPulse(trustPulse)}
            </p>
            {onOpenScorecard ? (
              <button
                type="button"
                onClick={onOpenScorecard}
                className="tap type-caption px-1 font-semibold text-fg-muted underline decoration-line underline-offset-2 transition hover:text-mint-400"
              >
                See the full record
              </button>
            ) : null}
          </div>
        ) : null}

        {footer ? <div className="mt-4 border-t border-line/60 pt-4">{footer}</div> : null}

        <div className="mt-4 border-t border-line/60 pt-4">
          <IndicatorTiles regime={regime} run={run} horizon={horizon} />
          <RotationStrip rotation={regime.sectorRotation ?? []} />
        </div>
      </TechnicalDisclosure>
    </section>
  );
}
