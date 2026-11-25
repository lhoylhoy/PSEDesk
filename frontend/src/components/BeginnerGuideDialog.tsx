import { Button } from './ui/Button';
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogStickyHeader,
  DialogTitle,
} from './ui/Dialog';

interface BeginnerGuideDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

const STEPS = [
  {
    title: '1 · Pick how long you want to hold',
    body: 'Quick (today), short-term (1–3 weeks), medium (1–3 months) or long-term (a year or more). Each choice gets its own list of stocks.',
  },
  {
    title: '2 · Read the three prices',
    body: 'Every card shows the most you should pay, the price to sell for a gain, and the price to sell and cut your loss. The desk never guesses these — it computes them from real market prices.',
  },
  {
    title: '3 · Practise before you risk real money',
    body: 'Press Record This Trade to log the plan in your practice account. It tracks what you would have made or lost, fees included, without touching a real broker.',
  },
];

const GLOSSARY = [
  {
    term: 'Cut-loss',
    meaning:
      'The price where you sell to stop a losing trade getting worse. It is set before you buy, not after.',
  },
  {
    term: 'Target',
    meaning: 'The price where the desk expects to take the profit on a trade.',
  },
  {
    term: 'Board lot',
    meaning:
      'The minimum number of shares you can buy in one order on the PSE. It changes with the share price, so the desk always rounds down.',
  },
  {
    term: 'Broker fees',
    meaning:
      'What your broker and the exchange charge to buy and sell. The desk subtracts them before it calls a trade worth taking.',
  },
  {
    term: 'Chance it reaches the target',
    meaning:
      'An estimate from past price behaviour, not a promise. Even a good plan loses sometimes.',
  },
  {
    term: 'Practice account',
    meaning:
      'A pretend wallet used to test the plans. No money leaves your bank and no order reaches a broker.',
  },
];

/** The plain-language "how does this work" sheet, reachable from the header on every screen. */
export function BeginnerGuideDialog({ open, onOpenChange }: BeginnerGuideDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="sm:w-[min(46rem,94vw)]">
        <DialogStickyHeader>
          <div className="min-w-0">
            <DialogTitle className="text-lg font-semibold text-white">
              How the PSE Signal Desk works
            </DialogTitle>
            <DialogDescription className="mt-1 max-w-xl text-xs leading-relaxed text-slate-400">
              A stock-research notebook for Philippine shares. For each name it reports the entry it would
              size, the target, and where its cut-loss would sit — in pesos, with broker fees already counted.
              It is not an adviser and does not place orders.
            </DialogDescription>
          </div>
          <DialogClose asChild>
            <Button variant="ghost">Close</Button>
          </DialogClose>
        </DialogStickyHeader>

        <ol className="mt-4 space-y-2">
          {STEPS.map((step) => (
            <li key={step.title} className="rounded-xl border border-line/70 bg-ink-850/60 px-3 py-2.5">
              <p className="text-xs font-semibold text-slate-100">{step.title}</p>
              <p className="mt-1 text-[11px] leading-relaxed text-slate-400">{step.body}</p>
            </li>
          ))}
        </ol>

        <section className="mt-4 rounded-xl border border-line/70 bg-ink-900/60 p-3">
          <h3 className="text-[11px] font-semibold text-slate-200">Words you will see</h3>
          <dl className="mt-2 grid gap-2 sm:grid-cols-2">
            {GLOSSARY.map((entry) => (
              <div key={entry.term}>
                <dt className="text-[11px] font-semibold text-mint-400">{entry.term}</dt>
                <dd className="mt-0.5 text-[11px] leading-relaxed text-slate-400">{entry.meaning}</dd>
              </div>
            ))}
          </dl>
        </section>

        <section className="mt-4 rounded-xl border border-amber-400/30 bg-amber-400/5 p-3">
          <h3 className="text-[11px] font-semibold text-amber-400">What it will not do</h3>
          <ul className="mt-1.5 space-y-1 text-[11px] leading-relaxed text-slate-400">
            <li>· It cannot promise a profit. Every plan can lose money.</li>
            <li>· It never places an order with your broker. You stay in control.</li>
            <li>· It does not use your real savings — only the practice amount you set.</li>
          </ul>
        </section>

        <div className="mt-4 flex flex-wrap items-center justify-between gap-2">
          <p className="text-[10px] leading-relaxed text-slate-600">
            Research tooling only, not investment advice. Prices and company filings come from live
            PSE/Yahoo Finance feeds.
          </p>
          <DialogClose asChild>
            <Button variant="primary">Got it</Button>
          </DialogClose>
        </div>
      </DialogContent>
    </Dialog>
  );
}
