import type { SignalItem } from '../types/signals';
import { cn } from '../lib/cn';
import { clamp, formatInteger, formatPercent, formatPrice, formatRatio } from '../lib/formatters';
import { computeRiskSizing, formatProbability, formatR, MODEL_POSITION_BUDGET_PHP } from '../lib/riskSizing';
import { VerdictBadge, verdictDescription } from './VerdictBadge';
import { Tooltip } from './ui/Tooltip';

interface RiskSizerBarProps {
  item: SignalItem;
  className?: string;
}

function SizerTile({ label, value, detail, tone }: { label: string; value: string; detail?: string; tone?: string }) {
  return (
    <div className="rounded-lg border border-line/60 bg-ink-900/60 px-2.5 py-1.5">
      <p className="text-[9px] uppercase tracking-[0.16em] text-slate-500">{label}</p>
      <p className={cn('tabular mt-0.5 text-xs font-semibold text-slate-100', tone)}>{value}</p>
      {detail ? <p className="tabular mt-0.5 text-[10px] text-slate-500">{detail}</p> : null}
    </div>
  );
}

/**
 * Visualises the server-owned odds next to the plan: win probability against the
 * fee-adjusted break-even rate, expected value per trade in R, the Quarter-Kelly budget
 * and the board-lot aligned share count that budget actually funds.
 */
export function RiskSizerBar({ item, className }: RiskSizerBarProps) {
  const sizing = computeRiskSizing(item);

  // Runs persisted before the probability ledger existed carry 0/0 odds; showing a 0% win
  // rate against a 0% break-even would read as a real (and terrible) forecast.
  if (!(item.winProbability > 0) || !(item.breakEvenWinRate > 0)) {
    return (
      <section className={cn('rounded-xl border border-line/70 bg-ink-850/60 p-3', className)}>
        <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
          Risk sizer · fee-adjusted · Quarter-Kelly
        </h3>
        <p className="mt-1.5 text-[11px] leading-relaxed text-slate-400">
          This run predates the probability ledger, so no odds, break-even rate or Kelly size were stored.
          Re-run the pipeline to have the desk price the setup and grade it forward.
        </p>
      </section>
    );
  }

  const winPct = clamp(item.winProbability * 100, 0, 100);
  const breakEvenPct = clamp(item.breakEvenWinRate * 100, 0, 100);
  const aboveBreakEven = item.winProbability >= item.breakEvenWinRate;
  const expectedValueTone = item.expectedValueR > 0 ? 'text-mint-400' : 'text-rose-400';

  return (
    <section className={cn('rounded-xl border border-line/70 bg-ink-850/60 p-3', className)}>
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div>
          <h3 className="text-[10px] uppercase tracking-[0.18em] text-slate-500">
            Risk sizer · fee-adjusted · Quarter-Kelly
          </h3>
          <p className="mt-0.5 text-[10px] text-slate-600">
            Probability and verdict are computed server-side; only the peso conversion happens here.
          </p>
        </div>
        <Tooltip label={verdictDescription(item.edgeVerdict)}>
          <span tabIndex={0}>
            <VerdictBadge verdict={item.edgeVerdict} />
          </span>
        </Tooltip>
      </div>

      <div className="mt-3">
        <div className="relative h-2 overflow-visible rounded-full bg-ink-700">
          <div
            className={cn('h-full rounded-full', aboveBreakEven ? 'bg-mint-500/80' : 'bg-rose-400/70')}
            style={{ width: `${Math.max(2, winPct)}%` }}
          />
          <span
            aria-hidden
            className="absolute -inset-y-1 w-0.5 rounded-full bg-white/70"
            style={{ left: `${breakEvenPct}%` }}
          />
        </div>
        <div className="mt-1.5 flex flex-wrap items-center justify-between gap-2 text-[10px] text-slate-500">
          <span className={cn('tabular font-semibold', aboveBreakEven ? 'text-mint-400' : 'text-rose-400')}>
            P(win) {formatProbability(item.winProbability)} · {sizing.edgeRatio.toFixed(2)}× the break-even rate
          </span>
          <span className="tabular">break-even {formatProbability(item.breakEvenWinRate)}</span>
        </div>
      </div>

      <dl className="mt-3 grid grid-cols-2 gap-2 sm:grid-cols-4">
        <SizerTile
          label="Net R:R"
          value={formatRatio(item.netRiskRewardRatio)}
          detail={`gross ${formatRatio(item.riskRewardRatio)}`}
        />
        <SizerTile
          label="Expected value"
          value={formatR(item.expectedValueR)}
          detail="per trade, after fees"
          tone={expectedValueTone}
        />
        <SizerTile
          label="Quarter-Kelly"
          value={`${sizing.kellyPct.toFixed(1)}%`}
          detail={sizing.budgetPhp > 0 ? formatPrice(sizing.budgetPhp) : 'no bet'}
        />
        <SizerTile
          label="Risk-sized"
          value={sizing.shares > 0 ? `${formatInteger(sizing.shares)} sh` : 'pass'}
          detail={
            sizing.shares > 0
              ? `${formatInteger(sizing.lots)} lot${sizing.lots === 1 ? '' : 's'} · ${formatPrice(sizing.grossRiskPhp)} at risk`
              : `budget ${formatPrice(MODEL_POSITION_BUDGET_PHP)} unused`
          }
          tone={sizing.shares > 0 ? 'text-slate-100' : 'text-slate-500'}
        />
      </dl>

      {sizing.shares === 0 && sizing.actionable ? (
        <p className="mt-2 text-[10px] text-amber-400/90">
          The Kelly budget cannot fund one {formatInteger(sizing.boardLot)}-share board lot at{' '}
          {formatPrice(item.entryPrice)}; treat this as a pass until the price or the budget changes.
        </p>
      ) : null}

      {sizing.serverSized && sizing.riskGovernor ? (
        <dl className="mt-2 grid grid-cols-2 gap-2 sm:grid-cols-3">
          <SizerTile
            label="Risk governor"
            value={sizing.riskGovernor}
            detail={
              sizing.effectiveRiskPct !== null
                ? `${sizing.effectiveRiskPct.toFixed(2)}% of equity at risk`
                : 'server-owned state'
            }
            tone={
              sizing.riskGovernor === 'HALTED' || sizing.riskGovernor === 'BLOCKED'
                ? 'text-rose-400'
                : sizing.riskGovernor === 'DEFENSIVE'
                  ? 'text-amber-400'
                  : 'text-mint-400'
            }
          />
          <SizerTile
            label="Slippage"
            value={formatPercent(sizing.slippageRatePct, 2, false)}
            detail={sizing.slippagePhp !== null ? `${formatPrice(sizing.slippagePhp)} both legs` : 'tick-grid estimate'}
            tone={
              sizing.slippageRatePct !== null && sizing.slippageRatePct >= 1 ? 'text-amber-400' : undefined
            }
          />
          <SizerTile
            label="Working the order"
            value={(sizing.executionGuidance ?? '—').replaceAll('_', ' ')}
            detail={
              sizing.advParticipationPct !== null
                ? `${sizing.advParticipationPct.toFixed(2)}% of 20D turnover`
                : 'no turnover reported'
            }
            tone={sizing.executionGuidance === 'WORK_THE_ORDER' ? 'text-amber-400' : undefined}
          />
        </dl>
      ) : null}

      {sizing.warnings.length > 0 ? (
        <ul className="mt-2 space-y-0.5">
          {sizing.warnings.map((warning) => (
            <li key={warning} className="text-[10px] leading-relaxed text-slate-500">
              · {warning}
            </li>
          ))}
        </ul>
      ) : null}
    </section>
  );
}
