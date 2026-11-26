import { useEffect, useState } from 'react';
import { portfolioApi } from '../api/signalsApi';
import type { PortfolioAccount } from '../types/portfolio';
import { cn } from '../lib/cn';
import { formatCompactPeso } from '../lib/formatters';
import {
  BUDGET_PRESETS_PHP,
  SAFETY_PRESETS,
  describeFriendlyError,
  describeRiskPerTrade,
  safetyPresetForSettings,
  type SafetyPresetId,
} from '../lib/plainAdvice';
import { Button } from './ui/Button';
import { InfoTip } from './ui/InfoTip';

export const WELCOME_DISMISSED_KEY = 'psedesk.welcomeDismissed';

function readDismissed(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  try {
    return window.localStorage.getItem(WELCOME_DISMISSED_KEY) === '1';
  } catch {
    return false;
  }
}

function storeDismissed(): void {
  try {
    window.localStorage.setItem(WELCOME_DISMISSED_KEY, '1');
  } catch {
    // A blocked storage quota only means the banner comes back next visit.
  }
}

interface WelcomeSetupBannerProps {
  /** The stored practice account; the banner stays hidden until the API has answered. */
  account: PortfolioAccount | null;
  onOpenGuide: () => void;
  onOpenTrades: () => void;
  /** Fired after a successful save so the terminal can refresh its book and alerts. */
  onSaved: () => void;
}

/**
 * First-run setup, in three questions a beginner can answer: how much play money, how much
 * risk per trade and whether the desk should slow down after a losing streak. It writes the
 * exact same account settings the Book tab edits — the server still validates and clamps
 * every value, and no sizing or P&L is computed here.
 */
export function WelcomeSetupBanner({
  account,
  onOpenGuide,
  onOpenTrades,
  onSaved,
}: WelcomeSetupBannerProps) {
  const [dismissed, setDismissed] = useState(readDismissed);
  const [budget, setBudget] = useState(account?.startingCapitalPhp ?? 100_000);
  const [presetId, setPresetId] = useState<SafetyPresetId>(
    safetyPresetForSettings(account ?? {})?.id ?? 'balanced',
  );
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Setup is a one-time job: once the account exists the banner collapses to a single calm
  // line so it never competes with the market call on a returning visit.
  const [expanded, setExpanded] = useState(false);

  // A fresh read of the book is the source of truth, so re-sync once it arrives.
  useEffect(() => {
    if (account) {
      setBudget(account.startingCapitalPhp);
      setPresetId(safetyPresetForSettings(account)?.id ?? 'balanced');
    }
  }, [account]);

  if (dismissed || !account) {
    return null;
  }

  const preset = SAFETY_PRESETS.find((entry) => entry.id === presetId) ?? SAFETY_PRESETS[1];
  const riskPct = preset.settings.maxRiskPerTradePct ?? account.maxRiskPerTradePct;

  function dismiss() {
    storeDismissed();
    setDismissed(true);
  }

  if (!expanded) {
    return (
      <section className="panel flex flex-wrap items-center justify-between gap-x-3 gap-y-2 px-4 py-2.5">
        <p className="type-body measure text-fg-muted">
          <span className="font-semibold text-fg">Practice account: </span>
          <span className="tabular">{formatCompactPeso(account.startingCapitalPhp)}</span> play money ·{' '}
          {riskPct.toFixed(2)}% risk per trade — every plan below is sized to fit.
        </p>
        <div className="flex items-center gap-3">
          <button
            type="button"
            aria-expanded={false}
            onClick={() => setExpanded(true)}
            className="text-2xs font-semibold text-fg-muted underline decoration-line underline-offset-2 transition hover:text-mint-400"
          >
            Change my setup
          </button>
          <button
            type="button"
            onClick={dismiss}
            className="text-2xs text-fg-subtle underline decoration-line underline-offset-2 transition hover:text-fg-muted"
          >
            Hide
          </button>
        </div>
      </section>
    );
  }

  async function save() {
    setSaving(true);
    setError(null);

    try {
      await portfolioApi.updateSettings({ startingCapitalPhp: budget, ...preset.settings });
      setSaved(true);
      onSaved();
    } catch (caught) {
      setError(describeFriendlyError(caught).message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <section className="panel border-mint-400/25 p-4 sm:p-5">
      <div className="flex flex-wrap items-start justify-between gap-3">
        <div className="max-w-2xl">
          <div className="flex flex-wrap items-center gap-2">
            <span className="rounded-full border border-mint-400/40 bg-mint-400/10 px-2 py-0.5 type-badge text-mint-400">
              New here?
            </span>
            <h2 className="text-sm font-semibold text-fg">
              Set up your practice account in three taps
            </h2>
          </div>
          <p className="mt-1.5 type-body measure text-fg-muted">
            This is a practice account: no real money moves. Tell the desk how much play money to
            assume and how careful it should be, and every buy plan on this page will be sized to
            fit.
          </p>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="secondary" onClick={onOpenGuide}>
            How this works
          </Button>
          <Button variant="secondary" onClick={() => setExpanded(false)}>
            Close setup
          </Button>
          <Button variant="ghost" onClick={dismiss}>
            Hide this
          </Button>
        </div>
      </div>

      <div className="mt-4 grid gap-4 lg:grid-cols-[1fr_1.4fr]">
        <div>
          <div className="flex items-center gap-1.5">
            <p className="type-eyebrow text-fg-subtle simple-label">
              1 · How much play money?
            </p>
            <InfoTip
              term="play money"
              label="The practice account starts with this much cash. Plans are sized so a single loss can only take a small slice of it."
            />
          </div>
          <div className="mt-2 flex flex-wrap gap-1.5">
            {BUDGET_PRESETS_PHP.map((amount) => (
              <button
                key={amount}
                type="button"
                aria-pressed={budget === amount}
                onClick={() => setBudget(amount)}
                className={cn(
                  'rounded-lg border px-2.5 py-1.5 type-caption font-semibold transition',
                  budget === amount
                    ? 'border-mint-400/50 bg-mint-400/10 text-mint-400'
                    : 'border-line/80 bg-ink-800/60 text-fg-muted hover:text-fg',
                )}
              >
                {formatCompactPeso(amount)}
              </button>
            ))}
          </div>
          <label className="mt-2 flex items-center gap-2 type-caption text-fg-subtle">
            <span>Or type it:</span>
            <span className="flex items-center gap-1 rounded-lg border border-line/80 bg-ink-900 px-2 py-1">
              <span aria-hidden className="text-fg-subtle">
                ₱
              </span>
              <input
                type="number"
                inputMode="numeric"
                min="1000"
                step="1000"
                value={budget}
                onChange={(event) => setBudget(Math.max(0, Number(event.target.value) || 0))}
                aria-label="Starting practice capital in pesos"
                className="tabular w-24 bg-transparent type-body text-fg outline-none"
              />
            </span>
          </label>
          <p className="mt-1.5 text-2xs leading-relaxed text-fg-faint">
            Changing this resets the cash balance only while the account is empty.
          </p>
        </div>

        <div>
          <div className="flex items-center gap-1.5">
            <p className="type-eyebrow text-fg-subtle simple-label">
              2 · How careful should the desk be?
            </p>
            <InfoTip
              term="risk per trade"
              label="How much of the account one bad trade may cost at the cut-loss. Smaller is safer, but the gains are smaller too."
            />
          </div>
          <div className="mt-2 grid gap-2 sm:grid-cols-3">
            {SAFETY_PRESETS.map((entry) => (
              <button
                key={entry.id}
                type="button"
                aria-pressed={entry.id === presetId}
                onClick={() => setPresetId(entry.id)}
                className={cn(
                  'rounded-xl border p-2.5 text-left transition',
                  entry.id === presetId
                    ? 'border-mint-400/50 bg-mint-400/5'
                    : 'border-line/80 bg-ink-850/60 hover:border-slate-500/50',
                )}
              >
                <p className="type-body font-semibold text-fg">{entry.label}</p>
                <p className="mt-0.5 text-2xs text-fg-subtle">{entry.tagline}</p>
                <p className="tabular mt-1 type-body font-semibold text-mint-400">
                  {entry.settings.maxRiskPerTradePct?.toFixed(2)}% risk
                </p>
                <p className="text-2xs text-fg-subtle">
                  {entry.settings.maxPortfolioHeatPct?.toFixed(0)}% total limit
                </p>
              </button>
            ))}
          </div>
          <p className="mt-2 type-body measure text-fg-muted">{preset.detail}</p>
          <p className="mt-1 text-2xs text-fg-subtle">
            {describeRiskPerTrade(preset.settings.maxRiskPerTradePct, budget)}
          </p>
        </div>
      </div>

      {error ? (
        <p className="mt-3 rounded-lg border border-rose-400/30 bg-rose-400/5 px-3.5 py-3 type-caption text-rose-400">
          {error}
        </p>
      ) : null}

      {saved ? (
        <p className="mt-3 rounded-lg border border-mint-400/30 bg-mint-400/5 px-3.5 py-3 type-caption text-mint-400">
          Saved. Every buy plan on this page now uses your play-money budget and safety level.
        </p>
      ) : null}

      <div className="mt-3 flex flex-wrap items-center justify-between gap-2">
        <p className="text-2xs text-fg-faint">
          You can change any of this later under My Trades → My Rules.
        </p>
        <div className="flex flex-wrap items-center gap-2">
          <Button variant="secondary" onClick={onOpenTrades}>
            See my practice account
          </Button>
          <Button variant="primary" busy={saving} onClick={() => void save()}>
            Save my setup
          </Button>
        </div>
      </div>
    </section>
  );
}
