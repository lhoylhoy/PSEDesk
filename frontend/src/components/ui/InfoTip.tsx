import type { ReactNode } from 'react';
import { cn } from '../../lib/cn';
import { Tooltip } from './Tooltip';

interface InfoTipProps {
  /** The explanation itself, written in plain language. */
  label: ReactNode;
  /** The term being explained, used for the accessible name. */
  term?: string;
  className?: string;
}

/**
 * The small "?" pill next to a number or a heading. It never replaces the figure it
 * explains — it only puts the meaning one hover (or focus) away, and stays reachable by
 * keyboard and screen readers.
 */
export function InfoTip({ label, term, className }: InfoTipProps) {
  return (
    <Tooltip label={label}>
      <button
        type="button"
        aria-label={term ? `What does ${term} mean?` : 'What does this mean?'}
        className={cn(
          // `relative` + the ::after ring grow the tap target to ~36px without changing the
          // 16px pill the desktop layout was designed around.
          'relative inline-flex size-4 shrink-0 items-center justify-center rounded-full border border-line bg-ink-800 text-[9px] font-semibold text-slate-400 transition after:absolute after:-inset-2.5 after:content-[""] hover:border-mint-400/50 hover:text-mint-400',
          className,
        )}
      >
        ?
      </button>
    </Tooltip>
  );
}
