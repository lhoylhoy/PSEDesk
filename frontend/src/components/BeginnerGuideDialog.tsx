import { keysForAction } from '../lib/shortcuts';
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
    body: 'Press Practise This Trade to log the plan in your practice account. It tracks what you would have made or lost, fees included, without touching a real broker.',
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
      {/* Prose sheet: it stays a single readable measure even on a 2560px monitor, so it
          opts out of the ultra-wide width step the tile-heavy dialogs get. */}
      <DialogContent className="sm:w-[min(46rem,94vw)] 3xl:w-[min(54rem,88vw)]">
        <DialogStickyHeader>
          <div className="min-w-0">
            <DialogTitle className="text-lg font-semibold text-fg">
              How the PSE Signal Desk works
            </DialogTitle>
            <DialogDescription className="mt-1 type-body measure text-fg-muted">
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
              <p className="type-body font-semibold text-fg">{step.title}</p>
              <p className="mt-1 type-body measure text-fg-muted">{step.body}</p>
            </li>
          ))}
        </ol>

        {/* The guide teaches the map without duplicating it: the keys come from the same list the
            shortcut sheet renders, so this sentence cannot drift out of date. */}
        <p className="mt-3 type-body text-fg-muted">
          On a keyboard, <kbd className="kbd">{keysForAction('horizon-day')[0]}</kbd>–
          <kbd className="kbd">{keysForAction('horizon-year')[0]}</kbd> switch how long you want to
          hold and <kbd className="kbd">{keysForAction('run')[0]}</kbd> runs the screen. Press{' '}
          <kbd className="kbd">{keysForAction('help')[0]}</kbd> for the whole map — every shortcut also
          has a button on screen, so nothing here requires a keyboard.
        </p>

        <section className="mt-4 rounded-xl border border-line/70 bg-ink-900/60 p-3">
          <h3 className="type-body font-semibold text-fg">Words you will see</h3>
          <dl className="mt-2 grid gap-2 sm:grid-cols-2">
            {GLOSSARY.map((entry) => (
              <div key={entry.term}>
                <dt className="type-body font-semibold text-mint-400">{entry.term}</dt>
                <dd className="mt-0.5 type-body measure text-fg-muted">{entry.meaning}</dd>
              </div>
            ))}
          </dl>
        </section>

        <section className="mt-4 rounded-xl border border-amber-400/30 bg-amber-400/5 p-3">
          <h3 className="type-body font-semibold text-amber-400">What it will not do</h3>
          <ul className="mt-1.5 space-y-1 type-body measure text-fg-muted">
            <li>· It cannot promise a profit. Every plan can lose money.</li>
            <li>· It never places an order with your broker. You stay in control.</li>
            <li>· It does not use your real savings — only the practice amount you set.</li>
          </ul>
        </section>

        <div className="mt-4 flex flex-wrap items-center justify-between gap-2">
          <p className="text-2xs leading-relaxed text-fg-faint">
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
