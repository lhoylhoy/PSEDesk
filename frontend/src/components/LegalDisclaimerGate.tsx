import { Dialog, DialogContent, DialogDescription, DialogTitle } from './ui/Dialog';
import { Button } from './ui/Button';
import { LEGAL_SECTIONS } from '../lib/legalConsent';

/**
 * The blocking first-run notice.
 *
 * It is deliberately un-dismissable: there is no close button, Escape and outside taps do nothing,
 * and the only way past it is the button that records the acceptance. That is the point — a reader
 * cannot reach a model verdict without first reading that the desk is not an adviser, that the
 * results are simulated, and what data leaves the machine.
 */
export function LegalDisclaimerGate({ onAccept }: { onAccept: () => void }) {
  return (
    <Dialog open>
      <DialogContent size="md">
        <div className="flex flex-col gap-4">
          <div>
            <p className="type-eyebrow text-fg-subtle">
              Before you use the desk
            </p>
            <DialogTitle className="mt-1 text-xl font-semibold text-fg">
              A research notebook, not an adviser
            </DialogTitle>
            <DialogDescription className="mt-2 text-sm leading-relaxed text-fg-muted">
              Five things worth knowing. The full text lives in{' '}
              <code className="rounded bg-ink-800 px-1 py-0.5 text-xs text-fg-muted">DISCLAIMER.md</code>,{' '}
              <code className="rounded bg-ink-800 px-1 py-0.5 text-xs text-fg-muted">PRIVACY.md</code> and{' '}
              <code className="rounded bg-ink-800 px-1 py-0.5 text-xs text-fg-muted">
                THIRD_PARTY_NOTICES.md
              </code>{' '}
              in the repository.
            </DialogDescription>
          </div>

          <ul className="flex flex-col gap-3">
            {LEGAL_SECTIONS.map((section) => (
              <li key={section.title} className="rounded-xl border border-line/70 bg-ink-800/50 px-3.5 py-3">
                <p className="type-eyebrow text-fg">
                  {section.title}
                </p>
                <p className="type-body measure mt-1.5 text-fg-muted">{section.body}</p>
              </li>
            ))}
          </ul>

          <div className="flex flex-col gap-2 border-t border-line/70 pt-3">
            <Button variant="primary" onClick={onAccept} className="w-full">
              I understand — open the desk
            </Button>
            <p className="text-center type-caption text-fg-subtle">
              Accepting stores one flag in this browser. There is no account, and leaving the desk is as
              simple as closing the tab.
            </p>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}
