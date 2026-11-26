import type { DropCategory, SignalDrop } from '../types/signals';
import { cn } from '../lib/cn';
import { companyLabel } from '../lib/pseNicknames';

const categoryStyles: Record<DropCategory, { label: string; friendlyLabel: string; className: string }> = {
  INVALIDATED: {
    label: 'Stop breached',
    friendlyLabel: 'Sell — cut-loss hit',
    className: 'border-rose-400/40 bg-rose-400/10 text-rose-400',
  },
  TARGET_HIT: {
    label: 'Target hit',
    friendlyLabel: 'Sold — target reached',
    className: 'border-mint-400/40 bg-mint-400/10 text-mint-400',
  },
  DISPLACED: {
    label: 'Displaced',
    friendlyLabel: 'Replaced by a stronger pick',
    className: 'border-amber-400/40 bg-amber-400/10 text-amber-400',
  },
};

interface DropPanelProps {
  drops: SignalDrop[];
  /** Simple mode: everyday wording instead of the desk's exit codes. */
  friendly?: boolean;
}

export function DropPanel({ drops, friendly = false }: DropPanelProps) {
  // Simple mode shows nothing at all when nothing changed: an empty big card is noise on a
  // calm day. One quiet line is enough to reassure the reader the desk checked.
  if (friendly && drops.length === 0) {
    return (
      <section className="panel px-4 py-3">
        <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
          <p className="text-[11px] font-semibold text-slate-300">Nothing changed on yesterday’s picks</p>
          <p className="text-[10px] text-slate-500">The same names are still in play — no action needed.</p>
        </div>
      </section>
    );
  }

  return (
    <section className="panel @container p-4 sm:p-5">
      <header className="flex flex-wrap items-baseline justify-between gap-2">
        <div>
          <h2 className="text-sm font-semibold text-slate-100">
            {friendly ? "Updates on Yesterday's Picks" : 'Dropped from yesterday'}
          </h2>
          <p className="mt-1 text-[11px] text-slate-500">
            {friendly
              ? 'These names left the list. If you owned them, this is what it means for you.'
              : 'Every exit is reconciled server-side: stop breached, target achieved, or displaced by a stronger setup.'}
          </p>
        </div>
        <span className="tabular rounded-full border border-line/80 bg-ink-850 px-2.5 py-1 text-[10px] font-semibold tracking-[0.14em] text-slate-400">
          {drops.length} {friendly ? 'UPDATE' : 'EXIT'}
          {drops.length === 1 ? '' : 'S'}
        </span>
      </header>

      {drops.length === 0 ? (
        <p className="mt-4 rounded-xl border border-dashed border-line/80 bg-ink-950/40 px-4 py-6 text-center text-xs text-slate-500">
          {friendly
            ? 'Nothing changed versus the last list — the same names are still in play.'
            : 'No names left the ranked list versus the previous session.'}
        </p>
      ) : (
        /* Container-driven, not viewport-driven: the same panel is a full-width section in
           Simple mode and a 22rem sidebar next to the Pro table on an ultra-wide monitor. */
        <ul className="mt-4 grid gap-3 @2xl:grid-cols-2 @5xl:grid-cols-3">
          {drops.map((drop) => {
            const style = categoryStyles[drop.dropCategory];

            return (
              <li
                key={`${drop.ticker}-${drop.previousRank ?? 'x'}`}
                className="flex flex-col gap-2 rounded-xl border border-line/70 bg-ink-850/70 p-3"
              >
                <div className="flex items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p
                      className={cn(
                        'text-sm font-semibold text-slate-100',
                        friendly ? 'leading-tight' : 'font-mono tracking-wide',
                      )}
                    >
                      {friendly ? companyLabel(drop.ticker, drop.companyName) : drop.ticker}
                    </p>
                    <p className="truncate text-[11px] text-slate-500">
                      {friendly ? `${drop.ticker} · ${drop.companyName}` : drop.companyName}
                    </p>
                  </div>
                  <span className="tabular shrink-0 rounded-md border border-line/70 bg-ink-900 px-1.5 py-0.5 text-[10px] text-slate-400">
                    {drop.previousRank != null ? `was #${drop.previousRank}` : 'unranked'}
                  </span>
                </div>

                <span
                  className={cn(
                    'inline-flex w-fit items-center rounded-full border px-2 py-0.5 text-[10px] font-semibold tracking-[0.12em] uppercase',
                    style.className,
                  )}
                >
                  {friendly ? style.friendlyLabel : style.label}
                </span>

                <p className="text-[11px] leading-relaxed text-slate-400">{drop.reason}</p>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
