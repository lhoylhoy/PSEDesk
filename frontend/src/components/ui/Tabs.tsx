import * as TabsPrimitive from '@radix-ui/react-tabs';
import type { ReactNode } from 'react';
import { cn } from '../../lib/cn';

interface TabsProps {
  value: string;
  onValueChange: (value: string) => void;
  children: ReactNode;
  className?: string;
}

export function Tabs({ value, onValueChange, children, className }: TabsProps) {
  return (
    <TabsPrimitive.Root value={value} onValueChange={onValueChange} className={className}>
      {children}
    </TabsPrimitive.Root>
  );
}

export function TabsList({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <TabsPrimitive.List
      className={cn(
        // On a phone the rail is wider than the screen: swipe it sideways rather than
        // wrapping it into three rows that push the content below the fold.
        'no-scrollbar inline-flex max-w-full items-center gap-1 overflow-x-auto overscroll-x-contain rounded-xl border border-line/80 bg-ink-900/80 p-1 sm:h-10',
        className,
      )}
    >
      {children}
    </TabsPrimitive.List>
  );
}

interface TabsTriggerProps {
  value: string;
  children: ReactNode;
  disabled?: boolean;
  title?: string;
  className?: string;
  /** Warm-up hooks: callers preload a tab's data on intent, never on mount. */
  onPointerEnter?: () => void;
  onFocus?: () => void;
}

export function TabsTrigger({
  value,
  children,
  disabled,
  title,
  className,
  onPointerEnter,
  onFocus,
}: TabsTriggerProps) {
  return (
    <TabsPrimitive.Trigger
      value={value}
      disabled={disabled}
      title={title}
      onPointerEnter={onPointerEnter}
      onFocus={onFocus}
      className={cn(
        // `sm:h-full` keeps every tab exactly as tall as the 40px rail (and the controls that
        // share its row); the phone rail stays 36px so the target is thumb-sized.
        'inline-flex min-h-9 shrink-0 items-center justify-center whitespace-nowrap rounded-lg px-3 py-1 text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-500 transition outline-none ui-tab sm:h-full sm:min-h-0',
        'hover:text-slate-200 disabled:cursor-not-allowed disabled:opacity-40',
        'data-[state=active]:bg-mint-400/10 data-[state=active]:text-mint-400 data-[state=active]:ring-1 data-[state=active]:ring-mint-400/30',
        className,
      )}
    >
      {children}
    </TabsPrimitive.Trigger>
  );
}
