import { HOW_TO_READ_STEPS, type HowToReadStepId } from '../lib/howToRead';

interface HowToReadStripProps {
  /** Focuses the market call above the list. */
  onReadCall: () => void;
  /** Opens the best-ranked idea, or research when the session ranked nothing. */
  onOpenSignal: () => void;
  /** Opens the paper blotter. */
  onPractise: () => void;
  /** Remembers the dismissal and hides the strip for next time. */
  onDismiss: () => void;
}

/**
 * The three-step path through the Simple screen, shown once above the list.
 *
 * It is not a tutorial in a drawer: each step is the action it describes. A first-time reader
 * can press "Open one stock" and land in the detail sheet without first working out that the
 * rows are tappable — which is the one thing every list-shaped app gets wrong.
 *
 * The strip is dismissible (the choice is remembered in `localStorage`) and the footer keeps a
 * permanent "How to read this screen" link, so hiding it never becomes losing it.
 */
export function HowToReadStrip({
  onReadCall,
  onOpenSignal,
  onPractise,
  onDismiss,
}: HowToReadStripProps) {
  const handlers: Record<HowToReadStepId, () => void> = {
    'read-call': onReadCall,
    'open-signal': onOpenSignal,
    practise: onPractise,
  };

  return (
    <section
      id="how-to-read"
      aria-labelledby="how-to-read-title"
      className="panel scroll-mt-4 border-sky-400/25 px-4 py-3.5 sm:px-5"
    >
      <div className="flex flex-wrap items-center justify-between gap-x-3 gap-y-1">
        <h2 id="how-to-read-title" className="type-title text-fg">
          New here? Three steps to read this screen
        </h2>
        <button
          type="button"
          onClick={onDismiss}
          className="type-caption font-semibold text-fg-subtle underline decoration-line underline-offset-2 transition hover:text-fg-muted"
        >
          Hide this
        </button>
      </div>

      <ol className="mt-3 grid gap-2 sm:grid-cols-3">
        {HOW_TO_READ_STEPS.map((step, index) => (
          <li key={step.id} className="min-w-0">
            <button
              type="button"
              onClick={handlers[step.id]}
              className="tap flex h-full w-full flex-col gap-1 rounded-xl border border-line/70 bg-ink-850/70 px-3.5 py-3 text-left transition hover:border-sky-400/40 hover:bg-ink-800/70 focus-visible:border-sky-400/50"
            >
              <span className="flex items-center gap-2">
                <span
                  aria-hidden
                  className="tabular flex size-5 shrink-0 items-center justify-center rounded-full border border-sky-400/40 bg-sky-400/10 type-badge text-sky-300"
                >
                  {index + 1}
                </span>
                <span className="type-body font-semibold text-fg simple-label">{step.title}</span>
              </span>
              <span className="type-caption measure text-fg-subtle simple-note">{step.body}</span>
              <span className="mt-auto pt-1 type-caption font-semibold text-sky-300">
                Do this <span aria-hidden>▸</span>
              </span>
            </button>
          </li>
        ))}
      </ol>
    </section>
  );
}
