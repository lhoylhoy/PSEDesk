import type { DriftStatus } from '../types/signals';
import { cn } from '../lib/cn';
import { describeDriftStatus } from '../lib/plainAdvice';

const driftStyles: Record<DriftStatus, { label: string; icon: string; className: string }> = {
  NEW: {
    label: 'NEW',
    icon: '◆',
    className: 'border-sky-400/40 bg-sky-400/10 text-sky-400',
  },
  HELD: {
    label: 'HELD',
    icon: '=',
    className: 'border-slate-500/40 bg-slate-500/10 text-fg-muted',
  },
  UPGRADED: {
    label: 'UP',
    icon: '▲',
    className: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
  },
  DOWNGRADED: {
    label: 'DOWN',
    icon: '▼',
    className: 'border-amber-400/40 bg-amber-400/10 text-amber-400',
  },
};

/** Row accent used by the signal table's left rail. */
export const driftAccent: Record<DriftStatus, string> = {
  NEW: 'bg-sky-400',
  HELD: 'bg-slate-600',
  UPGRADED: 'bg-mint-400',
  DOWNGRADED: 'bg-amber-400',
};

interface DriftBadgeProps {
  status: DriftStatus;
  previousRank?: number | null;
  /** Simple mode: "New pick" / "Steady" / "Moved up" / "Moved down". */
  friendly?: boolean;
}

export function DriftBadge({ status, previousRank, friendly = false }: DriftBadgeProps) {
  const style = driftStyles[status];
  const label = friendly ? describeDriftStatus(status) : style.label;

  return (
    <span className="inline-flex items-center gap-1.5 whitespace-nowrap">
      <span
        className={cn(
          'inline-flex items-center gap-1 rounded-full border px-2 py-0.5 type-badge',
          style.className,
        )}
      >
        <span aria-hidden className="text-2xs leading-none opacity-90">
          {style.icon}
        </span>
        {label}
      </span>
      {previousRank != null ? (
        <span className="tabular type-caption text-fg-subtle">was #{previousRank}</span>
      ) : null}
    </span>
  );
}
