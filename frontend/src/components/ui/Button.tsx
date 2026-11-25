import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { cn } from '../../lib/cn';

type ButtonVariant = 'primary' | 'secondary' | 'ghost';

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant;
  busy?: boolean;
  children: ReactNode;
}

const variantClasses: Record<ButtonVariant, string> = {
  primary:
    'border-mint-400/40 bg-mint-400/10 text-mint-400 hover:bg-mint-400/20 hover:border-mint-400/60',
  secondary:
    'border-line/80 bg-ink-800/80 text-slate-300 hover:border-slate-500/60 hover:text-white',
  ghost: 'border-transparent bg-transparent text-slate-400 hover:bg-ink-800 hover:text-white',
};

export function Button({ variant = 'secondary', busy, children, className, disabled, ...props }: ButtonProps) {
  return (
    <button
      type="button"
      disabled={disabled || busy}
      className={cn(
        'inline-flex h-10 items-center justify-center gap-2 rounded-xl border px-3.5 text-[11px] font-semibold uppercase tracking-[0.14em] transition ui-button',
        'select-none touch-manipulation active:scale-[0.98]',
        'disabled:cursor-not-allowed disabled:opacity-50',
        variantClasses[variant],
        className,
      )}
      {...props}
    >
      {busy ? (
        <span
          aria-hidden
          className="size-3 animate-spin rounded-full border-2 border-current border-t-transparent"
        />
      ) : null}
      {children}
    </button>
  );
}
