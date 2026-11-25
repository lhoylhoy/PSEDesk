import * as SelectPrimitive from '@radix-ui/react-select';
import { cn } from '../../lib/cn';

export interface SelectOption {
  value: string;
  label: string;
  hint?: string;
  disabled?: boolean;
}

interface SelectProps {
  value: string;
  options: SelectOption[];
  onChange: (value: string) => void;
  placeholder?: string;
  disabled?: boolean;
  ariaLabel?: string;
  className?: string;
}

/** Compact Radix select used for the trading-day picker. */
export function Select({
  value,
  options,
  onChange,
  placeholder = 'Select…',
  disabled,
  ariaLabel,
  className,
}: SelectProps) {
  return (
    <SelectPrimitive.Root value={value || undefined} onValueChange={onChange} disabled={disabled}>
      <SelectPrimitive.Trigger
        aria-label={ariaLabel}
        className={cn(
          // Full-width on phones (a 14rem trigger does not fit a 320px screen); compact from
          // `sm` up where the command bar has room for it.
          'flex h-10 w-full min-w-0 items-center justify-between gap-3 rounded-xl border border-line/80 bg-ink-900/80 px-3 text-xs font-medium text-slate-200 transition sm:inline-flex sm:w-auto sm:min-w-54',
          'hover:border-mint-400/40 disabled:cursor-not-allowed disabled:opacity-50',
          className,
        )}
      >
        <SelectPrimitive.Value placeholder={placeholder} />
        <SelectPrimitive.Icon className="text-[10px] text-slate-500">▼</SelectPrimitive.Icon>
      </SelectPrimitive.Trigger>
      <SelectPrimitive.Portal>
        <SelectPrimitive.Content
          position="popper"
          sideOffset={6}
          collisionPadding={12}
          className="z-50 max-h-80 max-w-[calc(100vw-1.5rem)] overflow-hidden rounded-xl border border-line bg-ink-850 shadow-2xl shadow-black/60"
        >
          <SelectPrimitive.Viewport className="p-1">
            {options.map((option) => (
              <SelectPrimitive.Item
                key={option.value}
                value={option.value}
                disabled={option.disabled}
                className={cn(
                  'flex cursor-pointer items-center justify-between gap-4 rounded-lg px-3 py-2 text-xs text-slate-300 outline-none',
                  'data-highlighted:bg-ink-700 data-highlighted:text-white',
                  'data-disabled:cursor-not-allowed data-disabled:opacity-40',
                )}
              >
                <SelectPrimitive.ItemText>{option.label}</SelectPrimitive.ItemText>
                {option.hint ? (
                  <span className="text-[10px] uppercase tracking-[0.14em] text-slate-500">{option.hint}</span>
                ) : null}
              </SelectPrimitive.Item>
            ))}
          </SelectPrimitive.Viewport>
        </SelectPrimitive.Content>
      </SelectPrimitive.Portal>
    </SelectPrimitive.Root>
  );
}
