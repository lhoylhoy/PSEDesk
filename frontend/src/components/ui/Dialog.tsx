import * as DialogPrimitive from '@radix-ui/react-dialog';
import type { ReactNode } from 'react';
import { cn } from '../../lib/cn';

export const Dialog = DialogPrimitive.Root;
export const DialogTrigger = DialogPrimitive.Trigger;
export const DialogClose = DialogPrimitive.Close;
export const DialogTitle = DialogPrimitive.Title;
export const DialogDescription = DialogPrimitive.Description;

/**
 * Content width once there is room for a centred modal; phones always get the full sheet.
 * The `3xl` step is the ultra-wide monitor: the same sheet gets a little more room rather
 * than floating as a small card in a 2560px window.
 */
type DialogSize = 'sm' | 'md' | 'lg' | 'xl';

const sizeClasses: Record<DialogSize, string> = {
  /** One column of reading: the shortcut map, a confirmation. */
  sm: 'sm:w-[min(40rem,94vw)] 3xl:w-[min(44rem,88vw)]',
  md: 'sm:w-[min(58rem,94vw)] 3xl:w-[min(64rem,88vw)]',
  lg: 'sm:w-[min(66rem,94vw)] 3xl:w-[min(76rem,86vw)]',
  xl: 'sm:w-[min(78rem,94vw)] 3xl:w-[min(92rem,86vw)]',
};

interface DialogContentProps {
  children: ReactNode;
  className?: string;
  size?: DialogSize;
}

/**
 * One dialog surface, two shapes. On a phone it is a bottom sheet — thumb-reachable, flush
 * with the screen edges and padded for the home indicator. From `sm` up it is the centred
 * modal the terminal has always used. The content itself never knows the difference.
 *
 * The sheet is also a **container-query root** (`@container`), so the tile grids inside it
 * respond to the sheet's own width instead of the viewport: a 94vw sheet on a 640px phone
 * lays out like a narrow card, and the same markup spreads to six columns on a 1056px
 * desktop sheet. That is why the dialogs reflow correctly on a landscape phone, a split
 * screen and a 2560px monitor from one class list.
 */
export function DialogContent({ children, className, size = 'md' }: DialogContentProps) {
  return (
    <DialogPrimitive.Portal>
      <DialogPrimitive.Overlay className="dialog-overlay fixed inset-0 z-40 bg-black/75 backdrop-blur-sm" />
      <DialogPrimitive.Content
        className={cn(
          'dialog-sheet @container fixed z-50 overflow-y-auto overscroll-contain border-line bg-ink-900 shadow-2xl shadow-black/70 outline-none',
          // Phone: bottom sheet. `dvh` tracks the collapsing browser chrome instead of the
          // largest possible viewport, so the sheet's last row is never behind the URL bar.
          'inset-x-0 bottom-0 max-h-[92dvh] w-full rounded-t-2xl border-t px-4 pb-safe pt-3',
          // Tablet and up: centred modal.
          'sm:inset-x-auto sm:bottom-auto sm:left-1/2 sm:top-1/2 sm:max-h-[88dvh] sm:-translate-x-1/2 sm:-translate-y-1/2 sm:rounded-2xl sm:border sm:p-6',
          sizeClasses[size],
          className,
        )}
      >
        <div
          aria-hidden
          className="mx-auto mb-2 h-1 w-10 shrink-0 rounded-full bg-line sm:hidden"
        />
        {children}
      </DialogPrimitive.Content>
    </DialogPrimitive.Portal>
  );
}

/**
 * The title row every dialog uses. It stays pinned while the body scrolls, so the close
 * button and the ticker/tab identity never leave the screen on a short phone viewport.
 */
export function DialogStickyHeader({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'sticky z-20 flex flex-wrap items-start justify-between gap-3 border-b border-line/70 bg-ink-900/95 backdrop-blur',
        '-top-3 -mx-4 mb-3 px-4 pb-3 pt-3',
        'sm:-top-6 sm:-mx-6 sm:mb-4 sm:gap-4 sm:px-6 sm:pb-4 sm:pt-6',
        className,
      )}
    >
      {children}
    </div>
  );
}

