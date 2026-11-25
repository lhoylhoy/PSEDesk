import type { SignalItem } from '../types/signals';
import { cn } from '../lib/cn';
import { formatPrice } from '../lib/formatters';
import { companyLabel } from '../lib/pseNicknames';

interface RidingNowStripProps {
  items: SignalItem[];
  onSelect: (ticker: string) => void;
}

/**
 * "What is inside the model's entry band right now?" — only the names whose server entry status
 * says the price is already inside the band. When nothing qualifies the strip renders nothing at
 * all, which is itself the honest answer: today the model sizes nothing.
 */
export function RidingNowStrip({ items, onSelect }: RidingNowStripProps) {
  const ready = items.filter(
    (item) => item.wavePlan?.entryStatus === 'READY_NOW' && item.edgeVerdict !== 'WATCH_ONLY',
  );

  if (ready.length === 0) {
    return null;
  }

  return (
    <section className="panel flex flex-col gap-2 px-4 py-2.5 sm:flex-row sm:items-center sm:justify-between">
      <p className="text-[10px] uppercase tracking-[0.18em] text-slate-500 simple-label">
        Inside the model's entry band
      </p>
      <ul className="flex flex-wrap items-center gap-1.5">
        {ready.slice(0, 5).map((item) => (
          <li key={item.ticker}>
            <button
              type="button"
              onClick={() => onSelect(item.ticker)}
              className={cn(
                'flex items-baseline gap-1.5 rounded-full border border-mint-400/40 bg-mint-400/10 px-2.5 py-1 text-[10px] transition hover:border-mint-400/70',
              )}
            >
              <span className="font-semibold text-mint-400">{item.ticker}</span>
              <span className="tabular text-slate-300">{formatPrice(item.entryPrice)}</span>
              <span className="hidden text-slate-500 sm:inline">{companyLabel(item.ticker, item.companyName)}</span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}
