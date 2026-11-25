import type { EdgeVerdict } from '../types/signals';
import { cn } from '../lib/cn';
import { describePlainVerdict } from '../lib/plainAdvice';

const verdictStyles: Record<
  EdgeVerdict,
  { label: string; icon: string; className: string; description: string }
> = {
  TRADE: {
    label: 'TRADE',
    icon: '●',
    className: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
    description: 'Clears the net R:R gate, the expected-value floor and the probability buffer.',
  },
  SMALL_SIZE: {
    label: 'SMALL',
    icon: '◐',
    className: 'border-amber-400/40 bg-amber-400/10 text-amber-400',
    description:
      'Positive expected value after fees, but a soft warning, the tape or a thin buffer argues for a reduced position.',
  },
  WATCH_ONLY: {
    label: 'WATCH',
    icon: '○',
    className: 'border-slate-500/40 bg-slate-500/10 text-slate-400',
    description:
      'Zero or negative expected value after the 0.690% round trip: a no-bet / pass kept for research context.',
  },
};

/** Row accent used by the signal table's left rail for no-bet names. */
export const verdictAccent: Record<EdgeVerdict, string> = {
  TRADE: 'bg-mint-400',
  SMALL_SIZE: 'bg-amber-400',
  WATCH_ONLY: 'bg-slate-600',
};

export function verdictDescription(verdict: EdgeVerdict | string): string {
  return verdictStyles[verdict as EdgeVerdict]?.description ?? 'Server-classified actionability verdict.';
}

interface VerdictBadgeProps {
  verdict: EdgeVerdict | string;
  className?: string;
  /** Simple mode: "Strong Buy" / "Buy Small" / "Wait" instead of the desk codes. */
  friendly?: boolean;
}

export function VerdictBadge({ verdict, className, friendly = false }: VerdictBadgeProps) {
  const style = verdictStyles[verdict as EdgeVerdict] ?? verdictStyles.WATCH_ONLY;
  const label = friendly ? describePlainVerdict(verdict).label : style.label;

  return (
    <span
      className={cn(
        'inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-[10px] font-semibold tracking-[0.12em]',
        style.className,
        className,
      )}
    >
      <span aria-hidden className="text-[8px] leading-none">
        {style.icon}
      </span>
      {label}
    </span>
  );
}
