import * as TooltipPrimitive from '@radix-ui/react-tooltip';
import { useEffect, useState, type ReactNode } from 'react';
import { cn } from '../../lib/cn';

export const TooltipProvider = TooltipPrimitive.Provider;

/** True on touch-first devices, where "hover" never happens and a tap must open the bubble. */
function useCoarsePointer(): boolean {
  const [coarse, setCoarse] = useState(false);

  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
      return;
    }

    const query = window.matchMedia('(pointer: coarse)');
    const update = () => setCoarse(query.matches);

    update();
    query.addEventListener('change', update);

    return () => query.removeEventListener('change', update);
  }, []);

  return coarse;
}

interface TooltipProps {
  label: ReactNode;
  children: ReactNode;
  side?: 'top' | 'right' | 'bottom' | 'left';
  align?: 'start' | 'center' | 'end';
  className?: string;
}

/**
 * Radix tooltip with the terminal's dark panel styling. The child must be focusable.
 *
 * On a coarse pointer the bubble becomes tap-to-toggle instead of hover-only, otherwise
 * every `?` pill in the Simple view is dead weight on a phone. A later tap anywhere on the
 * page, or Escape, dismisses it.
 */
export function Tooltip({ label, children, side = 'top', align = 'center', className }: TooltipProps) {
  const coarse = useCoarsePointer();
  const [open, setOpen] = useState(false);

  useEffect(() => {
    if (!coarse || !open) {
      return;
    }

    const close = () => setOpen(false);
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        close();
      }
    };

    // Defer one tick: the same tap that opened the bubble must not immediately close it.
    const timer = window.setTimeout(() => {
      window.addEventListener('pointerdown', close);
      window.addEventListener('keydown', onKeyDown);
    }, 0);

    return () => {
      window.clearTimeout(timer);
      window.removeEventListener('pointerdown', close);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [coarse, open]);

  return (
    <TooltipPrimitive.Root
      delayDuration={150}
      open={coarse ? open : undefined}
      onOpenChange={coarse ? setOpen : undefined}
    >
      <TooltipPrimitive.Trigger
        asChild
        onClick={coarse ? () => setOpen((current) => !current) : undefined}
      >
        {children}
      </TooltipPrimitive.Trigger>
      <TooltipPrimitive.Portal>
        <TooltipPrimitive.Content
          side={side}
          align={align}
          sideOffset={8}
          collisionPadding={12}
          className={cn(
            'z-50 max-w-[min(22rem,calc(100vw-1.5rem))] rounded-xl border border-line bg-ink-800 px-3 py-2 text-xs leading-relaxed text-slate-300 shadow-2xl shadow-black/60 ui-tooltip',
            className,
          )}
        >
          {label}
          <TooltipPrimitive.Arrow className="fill-ink-800" width={10} height={5} />
        </TooltipPrimitive.Content>
      </TooltipPrimitive.Portal>
    </TooltipPrimitive.Root>
  );
}
