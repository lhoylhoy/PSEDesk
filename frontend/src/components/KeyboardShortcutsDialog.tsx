import { SHORTCUT_GROUPS } from '../lib/shortcuts';
import { Button } from './ui/Button';
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogStickyHeader,
  DialogTitle,
} from './ui/Dialog';

interface KeyboardShortcutsDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

/**
 * The keyboard map, rendered from the same list the handler reads.
 *
 * It is the only place a reader can discover that `1`–`4` switch horizon or that `R` runs the
 * screen, so it is reachable from three places that cost nothing: the `?` key, the `More` menu
 * and the ☰ sheet on a phone. Nothing here is a hidden feature — every row names a control that
 * is also a visible button on screen.
 */
export function KeyboardShortcutsDialog({ open, onOpenChange }: KeyboardShortcutsDialogProps) {
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent size="sm">
        <DialogStickyHeader>
          <div className="min-w-0">
            <DialogTitle className="text-lg font-semibold text-fg">Keyboard shortcuts</DialogTitle>
            <DialogDescription className="mt-1 type-body measure text-fg-muted">
              Faster than hunting for a button. Nothing here is keyboard-only — every shortcut has a
              visible control, and none of them fire while you are typing in a box.
            </DialogDescription>
          </div>
          <DialogClose asChild>
            <Button variant="ghost">Close</Button>
          </DialogClose>
        </DialogStickyHeader>

        <div className="flex flex-col gap-4">
          {SHORTCUT_GROUPS.map((group) => (
            <section key={group.title}>
              <h3 className="type-eyebrow text-fg-subtle">{group.title}</h3>
              <dl className="mt-1.5">
                {group.items.map((item) => (
                  <div
                    key={item.label}
                    className="flex items-center justify-between gap-3 border-b border-line/40 py-2 last:border-b-0"
                  >
                    <dt className="type-body min-w-0 text-fg-muted">{item.label}</dt>
                    <dd className="flex shrink-0 items-center gap-1">
                      {item.keys.map((key) => (
                        <kbd key={key} className="kbd">
                          {key}
                        </kbd>
                      ))}
                    </dd>
                  </div>
                ))}
              </dl>
            </section>
          ))}
        </div>

        <p className="mt-4 type-caption text-fg-faint">
          Shortcuts pause while you are typing, while a sheet is open and while the browser owns the
          key (⌘ or Ctrl held). Escape always closes the sheet in front of you.
        </p>
      </DialogContent>
    </Dialog>
  );
}
