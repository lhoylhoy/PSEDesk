import { useEffect, useId, useState } from 'react';
import type { ReactNode } from 'react';
import { cn } from '../../lib/cn';

interface TechnicalDisclosureProps {
  /** Toggle label, e.g. "Technical details". */
  title?: string;
  /** Optional pills shown while collapsed, so a curious user can preview the depth. */
  summary?: ReactNode;
  /** Expanded by default in Pro mode; collapsed in Simple mode. */
  defaultOpen?: boolean;
  children: ReactNode;
  className?: string;
}

/**
 * Accessible progressive-disclosure container. The dense quant detail stays one click
 * away instead of dominating the screen, and Pro mode simply opens it by default.
 */
export function TechnicalDisclosure({
  title = 'Technical details',
  summary,
  defaultOpen = false,
  children,
  className,
}: TechnicalDisclosureProps) {
  const [open, setOpen] = useState(defaultOpen);
  const panelId = useId();

  // Switching Simple ↔ Pro flips the default for every drawer on the page. A manual toggle
  // is only replaced when the mode itself changes, never on an ordinary re-render.
  useEffect(() => {
    setOpen(defaultOpen);
  }, [defaultOpen]);

  return (
    <div className={cn('rounded-xl border border-line/70 bg-ink-850/50', className)}>
      <button
        type="button"
        aria-expanded={open}
        aria-controls={panelId}
        onClick={() => setOpen((current) => !current)}
        className="tap flex w-full flex-wrap items-center justify-between gap-2 px-3.5 py-3 text-left transition hover:bg-ink-800/60"
      >
        <span className="flex flex-wrap items-center gap-2">
          <span aria-hidden className="text-2xs text-fg-subtle">
            {open ? '▾' : '▸'}
          </span>
          <span className="type-caption font-semibold text-fg-muted ui-drawer-title">{title}</span>
        </span>
        {summary && !open ? <span className="flex flex-wrap items-center gap-1.5">{summary}</span> : null}
      </button>

      {open ? (
        <div id={panelId} className="border-t border-line/60 px-3.5 py-3.5">
          {children}
        </div>
      ) : null}
    </div>
  );
}

/** Small monospace pill for technical drawer summaries. */
export function DisclosurePill({ children }: { children: ReactNode }) {
  return (
    <span className="tabular type-caption rounded border border-line/70 bg-ink-900/80 px-1.5 py-0.5 text-fg-muted">
      {children}
    </span>
  );
}

/**
 * Renders `children` inside a drawer when `enabled`, and inline when not — so the same
 * markup can serve Simple mode (collapsed depth) and Pro mode (everything on screen).
 */
export function OptionalDisclosure({
  enabled,
  title,
  className,
  children,
}: {
  enabled: boolean;
  title: string;
  className?: string;
  children: ReactNode;
}) {
  if (!enabled) {
    return <>{children}</>;
  }

  return (
    <TechnicalDisclosure title={title} className={cn('mt-4', className)}>
      <div className="space-y-4">{children}</div>
    </TechnicalDisclosure>
  );
}
