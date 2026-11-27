import { useEffect, useRef, useState, type MouseEvent, type ReactNode } from 'react';
import type { DateOption, Horizon } from '../types/signals';
import { cn } from '../lib/cn';
import { formatWeekday } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import { describeRunLabel } from '../lib/plainAdvice';
import {
  prefetchHorizon,
  preloadBeginnerGuideDialog,
  preloadKeyboardShortcutsDialog,
  preloadKnowledgeBaseDialog,
  preloadPortfolioDialog,
  preloadResearchDialog,
  preloadScorecardDialog,
  preloadSignalTable,
} from '../lib/preloaders';
import { keysForAction } from '../lib/shortcuts';
import type { ThemeMode } from '../lib/theme';
import type { ViewMode } from '../lib/viewMode';
import { Button } from './ui/Button';
import { DeskLogo } from './DeskLogo';
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogStickyHeader,
  DialogTitle,
} from './ui/Dialog';
import { Select } from './ui/Select';
import { Tabs, TabsList, TabsTrigger } from './ui/Tabs';

interface TerminalHeaderProps {
  dates: DateOption[];
  selectedDate: string | null;
  onDateChange: (date: string) => void;
  /**
   * True when the reader has stepped back to an older stored session. The primary action then
   * re-runs that day instead of claiming to fetch the latest advice.
   */
  historicalSession?: boolean;
  horizon: Horizon;
  onHorizonChange: (horizon: Horizon) => void;
  onRunNow: () => void;
  onOpenKnowledge: () => void;
  onOpenScorecard: () => void;
  onOpenResearch: () => void;
  onOpenPortfolio: () => void;
  /** Plain-language "how this works" sheet. */
  onOpenGuide: () => void;
  /** The keyboard map; reachable from the More menu and the phone sheet. */
  onOpenShortcuts: () => void;
  /** Server-side exit alerts waiting in the blotter; surfaced as a count. */
  portfolioAlerts?: number;
  busy: 'run' | null;
  /** `simple` (default) shows everyday advice; `pro` restores the dense terminal. */
  viewMode: ViewMode;
  onViewModeChange: (mode: ViewMode) => void;
  /** Light/dark presentation; both themes show exactly the same data. */
  theme: ThemeMode;
  onThemeChange: (theme: ThemeMode) => void;
  /** The DQR import control, hoisted so the phone sheet can reach it on Pro. */
  quotationImport?: ReactNode;
}

type SegmentOption<T extends string> = {
  value: T;
  label: string;
  /** Decorative leading glyph (the sun/moon pair). */
  icon?: string;
  /** Keyboard accelerator documented on the segment, e.g. `S`. */
  shortcut?: string;
  /** Warm-up hook for a heavier mode; never fired on mount. */
  preload?: () => void;
};

/**
 * One 40px-tall segmented switch. Both appearance controls are the same control with different
 * options, so height, radius and the Simple-mode type floor live in exactly one place — and a
 * desktop row of segments lines up with the 40px buttons and date picker beside it.
 */
function SegmentedControl<T extends string>({
  label,
  hint,
  value,
  options,
  onChange,
}: {
  label: string;
  /** Plain-language explanation of what the switch changes, read on hover and by a reader. */
  hint?: string;
  value: T;
  options: SegmentOption<T>[];
  onChange: (value: T) => void;
}) {
  return (
    <div
      role="group"
      aria-label={label}
      aria-description={hint}
      title={hint}
      className="inline-flex items-center gap-0.5 rounded-xl border border-line/80 bg-ink-900/80 p-1 sm:h-10"
    >
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-pressed={value === option.value}
          aria-keyshortcuts={option.shortcut}
          onClick={() => onChange(option.value)}
          onPointerEnter={option.preload}
          onFocus={option.preload}
          className={cn(
            // `sm:h-full` makes the inner pill exactly fill the 40px frame, so segments never
            // float shorter than the buttons next to them. `ui-button` lifts the label to the
            // Simple-mode type floor (Pro keeps the 10px terminal scale).
            'inline-flex min-h-9 items-center rounded-lg px-3 type-badge transition ui-button sm:h-full sm:min-h-0 sm:px-2.5',
            value === option.value
              ? 'bg-mint-400/10 text-mint-400 ring-1 ring-mint-400/30'
              : 'text-fg-subtle hover:text-fg',
          )}
        >
          {option.icon ? (
            <span aria-hidden className="mr-1">
              {option.icon}
            </span>
          ) : null}
          {option.label}
        </button>
      ))}
    </div>
  );
}

/** Simple | Pro segmented switch: the only control that changes how much depth is on screen. */
function ViewModeSwitch({ value, onChange }: { value: ViewMode; onChange: (mode: ViewMode) => void }) {
  return (
    <SegmentedControl
      label="Detail level"
      hint="Simple lists today’s calls — tap a row to open its detail sheet. Pro is the full quant table: odds, R:R, sizing and every indicator."
      value={value}
      onChange={onChange}
      options={[
        { value: 'simple', label: 'Simple', shortcut: keysForAction('view-simple').join(' ') },
        { value: 'pro', label: 'Pro', shortcut: keysForAction('view-pro').join(' '), preload: preloadSignalTable },
      ]}
    />
  );
}

/** Sun/moon switch. Both palettes are defined in index.css, so this only flips the attribute. */
function ThemeSwitch({ value, onChange }: { value: ThemeMode; onChange: (theme: ThemeMode) => void }) {
  return (
    <SegmentedControl
      label="Colour theme"
      value={value}
      onChange={onChange}
      options={[
        { value: 'light', label: 'Light', icon: '☀' },
        { value: 'dark', label: 'Dark', icon: '☾' },
      ]}
    />
  );
}

/** Secondary desk sheets live behind one calm menu so the command bar stays two rows. */
function MoreMenu({
  simple,
  busy,
  onOpenGuide,
  onOpenShortcuts,
  onOpenScorecard,
  onOpenResearch,
  onOpenKnowledge,
  quotationImport,
}: {
  simple: boolean;
  busy: boolean;
  onOpenGuide: () => void;
  onOpenShortcuts: () => void;
  onOpenScorecard: () => void;
  onOpenResearch: () => void;
  onOpenKnowledge: () => void;
  quotationImport?: ReactNode;
}) {
  const menuRef = useRef<HTMLDetailsElement>(null);

  /**
   * A native `<details>` closes only when its own summary is pressed, so an open menu used to
   * stay on screen after a click anywhere else. One listener pair closes it on the next outside
   * press (capture phase, so it lands before whatever is underneath acts) and on Escape — which
   * also returns focus to the summary so the keyboard path continues where it started.
   */
  useEffect(() => {
    const closeIfOutside = (event: PointerEvent) => {
      const menu = menuRef.current;
      if (!menu?.open || (event.target instanceof Node && menu.contains(event.target))) {
        return;
      }
      menu.removeAttribute('open');
    };

    const closeOnEscape = (event: KeyboardEvent) => {
      const menu = menuRef.current;
      if (event.key !== 'Escape' || !menu?.open) {
        return;
      }
      menu.removeAttribute('open');
      menu.querySelector('summary')?.focus();
    };

    document.addEventListener('pointerdown', closeIfOutside, true);
    document.addEventListener('keydown', closeOnEscape);
    return () => {
      document.removeEventListener('pointerdown', closeIfOutside, true);
      document.removeEventListener('keydown', closeOnEscape);
    };
  }, []);

  const closeThen = (action: () => void) => (event: MouseEvent<HTMLButtonElement>) => {
    event.currentTarget.closest('details')?.removeAttribute('open');
    action();
  };

  const itemClass =
    'flex w-full items-center justify-between gap-2 rounded-lg px-3 py-2.5 text-left type-body font-medium text-fg-muted transition hover:bg-ink-700 hover:text-fg disabled:cursor-not-allowed disabled:opacity-40';
  // Two tiny group labels replace a flat list of four unrelated sheets, so "what is this desk"
  // and "show me a result" stop reading as the same kind of thing.
  const groupClass =
    'type-eyebrow px-3 pt-2.5 pb-1 text-fg-faint';

  return (
    <details ref={menuRef} className="group relative">
      <summary className="flex h-10 cursor-pointer list-none items-center gap-1 rounded-xl border border-line/80 bg-ink-900/80 px-3 text-xs font-semibold text-fg transition select-none ui-button hover:border-mint-400/40">
        More
        <span aria-hidden className="text-2xs text-fg-subtle transition group-open:rotate-180">
          ▾
        </span>
      </summary>
      <div className="absolute right-0 z-40 mt-2 w-64 rounded-xl border border-line bg-ink-850 p-1.5 shadow-2xl shadow-black/60">
        <p className={groupClass}>Learn</p>
        <button
          type="button"
          className={itemClass}
          disabled={busy}
          onClick={closeThen(onOpenGuide)}
          onPointerEnter={preloadBeginnerGuideDialog}
          onFocus={preloadBeginnerGuideDialog}
        >
          {simple ? 'How it works' : 'Guide'}
        </button>
        {/* Shortcuts sit next to the guide: both answer "how do I drive this?", and the sheet
            teaches the map rather than making the reader guess at hidden keys. */}
        <button
          type="button"
          className={itemClass}
          disabled={busy}
          onClick={closeThen(onOpenShortcuts)}
          onPointerEnter={preloadKeyboardShortcutsDialog}
          onFocus={preloadKeyboardShortcutsDialog}
        >
          <span>Keyboard shortcuts</span>
          <kbd className="kbd">?</kbd>
        </button>
        {simple ? null : (
          <button
            type="button"
            className={itemClass}
            disabled={busy}
            onClick={closeThen(onOpenKnowledge)}
            onPointerEnter={preloadKnowledgeBaseDialog}
            onFocus={preloadKnowledgeBaseDialog}
          >
            Knowledge base
          </button>
        )}

        <p className={cn(groupClass, 'mt-1 border-t border-line/60')}>Tools</p>
        <button
          type="button"
          className={itemClass}
          disabled={busy}
          onClick={closeThen(onOpenResearch)}
          onPointerEnter={preloadResearchDialog}
          onFocus={preloadResearchDialog}
        >
          {simple ? 'Check a Stock' : 'Research'}
        </button>
        <button
          type="button"
          className={itemClass}
          disabled={busy}
          onClick={closeThen(onOpenScorecard)}
          onPointerEnter={() => preloadScorecardDialog('DAY')}
        >
          {simple ? 'Past Results' : 'Scorecard'}
        </button>
        {quotationImport && !simple ? (
          <div className="mt-1 border-t border-line/60 p-2">{quotationImport}</div>
        ) : null}
      </div>
    </details>
  );
}

const horizonOrder: Horizon[] = ['DAY', 'WEEK', 'MONTH', 'YEAR'];

export function TerminalHeader({
  dates,
  selectedDate,
  onDateChange,
  historicalSession = false,
  horizon,
  onHorizonChange,
  onRunNow,
  onOpenKnowledge,
  onOpenScorecard,
  onOpenResearch,
  onOpenPortfolio,
  onOpenGuide,
  onOpenShortcuts,
  portfolioAlerts = 0,
  busy,
  viewMode,
  onViewModeChange,
  theme,
  onThemeChange,
  quotationImport,
}: TerminalHeaderProps) {
  const [menuOpen, setMenuOpen] = useState(false);
  const simple = viewMode === 'simple';

  // The primary action never says "latest" while a stored session is on screen: it re-runs the
  // day being read, which is a different thing and must be labelled as such.
  const runLabel = describeRunLabel(historicalSession, simple);

  // The full sentence stays reachable on hover/focus; the visible line is short enough to hold
  // one line at 1024px, where the old `max-w-md` sentence wrapped and outgrew the 40px row.
  const tagline = simple
    ? 'Clear buy, target and cut-loss plans for Philippine stocks — PSE fees already factored in.'
    : 'Probabilistic desk · fee-adjusted odds, server-sized tickets, a paper blotter and a self-grading scorecard';
  const shortTagline = simple
    ? 'Buy, target and cut-loss plans — fees included.'
    : 'Fee-adjusted odds, server-sized tickets, graded results';

  // The newest stored session is what "latest" means. Label it in the picker so a reader who
  // stepped into history can always find the way back.
  const datesNewest = dates.reduce<string | null>(
    (newest, option) => (newest == null || option.date > newest ? option.date : newest),
    null,
  );

  const options = [...dates]
    .sort((left, right) => right.date.localeCompare(left.date))
    .map((option) => ({
      value: option.date,
      label: formatWeekday(option.date),
      hint: `${option.date === datesNewest ? 'latest · ' : ''}${
        option.hasSignalRun ? (simple ? 'advice' : 'run') : simple ? 'prices' : 'data'
      }`,
    }));

  /** Sheet actions close the sheet first so the dialog it opens owns the screen. */
  const sheetAction = (action: () => void) => () => {
    setMenuOpen(false);
    action();
  };

  const datePicker = (className?: string) => (
    <Select
      ariaLabel="Trading day"
      value={selectedDate ?? ''}
      options={options}
      onChange={onDateChange}
      placeholder={dates.length === 0 ? 'No sessions saved yet' : 'Select a day'}
      disabled={dates.length === 0 || busy !== null}
      className={className}
    />
  );

  const alertBadge =
    portfolioAlerts > 0 ? (
      <span className="type-caption rounded-full bg-amber-400/20 px-1.5 font-semibold text-amber-400">
        {portfolioAlerts}
      </span>
    ) : null;

  return (
    // `.panel`'s backdrop-blur makes the header a stacking context, so the `More` panel's own
    // z-40 only ranks *inside* it: without a rung of its own the header painted below the
    // in-flow `.panel` cards it overlaps and the open menu was swallowed by the call bar.
    // `relative z-30` lifts the whole header above them (z-auto = 0) while staying under every
    // portalled overlay (Dialog / Select / Tooltip) on the z-50 rung.
    <header className="panel relative z-30 flex flex-col gap-3 p-4 pt-[max(0.75rem,env(safe-area-inset-top))] sm:p-5 lg:gap-4">
      {/* Phone & small tablet: two thumb-sized rows instead of nine wrapped controls. */}
      <div className="flex flex-col gap-2 lg:hidden">
        <div className="flex items-center gap-2">
          <DeskLogo size="sm" />
          <div className="min-w-0">
            <h1 className="truncate text-lg font-semibold tracking-tight text-fg">
              PSE Signal Desk
            </h1>
            <p className="type-caption truncate text-fg-subtle">
              {simple
                ? 'Clear buy, target and cut-loss plans'
                : 'Probabilistic desk · server-owned odds'}
            </p>
          </div>
          <Button
            variant="secondary"
            aria-label="Open desk controls"
            aria-expanded={menuOpen}
            onClick={() => setMenuOpen(true)}
            className="ml-auto size-10 shrink-0 px-0"
          >
            <span aria-hidden className="text-base leading-none">
              ☰
            </span>
          </Button>
        </div>
        <div className="grid grid-cols-[minmax(0,1fr)_auto] gap-2 md:grid-cols-[auto_minmax(0,1fr)_auto]">
          {/* From `md` the tablet command row gets the trading-day picker inline: a 768px
              screen has room for three thumb-sized controls, and picking a stored session
              should not require opening the sheet. The wrapper owns the `hidden`, so the
              trigger's own `sm:inline-flex` cannot re-show it inside a 2-column grid. */}
          <div className="hidden md:block">{datePicker()}</div>
          <Button
            variant="cta"
            busy={busy === 'run'}
            onClick={onRunNow}
            disabled={busy !== null}
            className="w-full min-w-0"
          >
            {/* A 320px phone cannot fit “Run Latest Screen” and “My Trades” side by side
                at the Simple-mode type floor, so the label ellipsizes instead of pushing
                the second control off the screen. */}
            <span className="min-w-0 truncate">{busy === 'run' ? 'Running…' : runLabel}</span>
          </Button>
          <Button
            variant="secondary"
            onClick={onOpenPortfolio}
            onPointerEnter={preloadPortfolioDialog}
            onFocus={preloadPortfolioDialog}
            disabled={busy !== null}
            className="whitespace-nowrap"
          >
            {simple ? 'My Trades' : 'Book'}
            {alertBadge}
          </Button>
        </div>
      </div>

      {/* Desktop command bar: identity and session on the left, the action cluster on the right.
          One 40px control height and one divider between "which day" and "do something"; the
          appearance switches live on the row below so this line never has to wrap. */}
      <div className="hidden items-center justify-between gap-4 lg:flex">
        <div className="flex min-w-0 items-center gap-3">
          <DeskLogo />
          <div className="min-w-0 max-w-md">
            <h1 className="text-xl font-semibold tracking-tight text-fg">PSE Signal Desk</h1>
            <p className="type-caption truncate text-fg-subtle" title={tagline}>
              {shortTagline}
            </p>
          </div>
        </div>

        <div className="flex flex-wrap items-center justify-end gap-2">
          {datePicker()}
          <span aria-hidden className="h-6 w-px shrink-0 bg-line/70" />
          <Button
            variant="secondary"
            onClick={onOpenPortfolio}
            aria-keyshortcuts={keysForAction('trades').join(' ')}
            onPointerEnter={preloadPortfolioDialog}
            onFocus={preloadPortfolioDialog}
            disabled={busy !== null}
          >
            {simple ? 'My Trades' : 'Book'}
            {alertBadge}
          </Button>
          <Button
            variant="cta"
            busy={busy === 'run'}
            onClick={onRunNow}
            aria-keyshortcuts={keysForAction('run').join(' ')}
            disabled={busy !== null}
          >
            {busy === 'run' ? 'Running…' : runLabel}
          </Button>
          <MoreMenu
            simple={simple}
            busy={busy !== null}
            onOpenGuide={onOpenGuide}
            onOpenShortcuts={onOpenShortcuts}
            onOpenScorecard={onOpenScorecard}
            onOpenResearch={onOpenResearch}
            onOpenKnowledge={onOpenKnowledge}
            quotationImport={quotationImport}
          />
        </div>
      </div>

      <div className="flex flex-col gap-2 border-t border-line/60 pt-3 sm:flex-row sm:items-center sm:justify-between sm:gap-3">
        <div className="flex min-w-0 flex-col gap-2 sm:flex-row sm:items-center sm:gap-3">
          {simple ? (
            <span className="type-caption hidden text-fg-subtle xl:inline">
              How long do you want to hold?
            </span>
          ) : null}
          <Tabs
            value={horizon}
            onValueChange={(value) => onHorizonChange(value as Horizon)}
            className="min-w-0 max-w-full"
          >
            <TabsList>
              {horizonOrder.map((option, index) => (
                <TabsTrigger
                  key={option}
                  value={option}
                  keyShortcut={String(index + 1)}
                  onPointerEnter={() => prefetchHorizon(option, null)}
                  onFocus={() => prefetchHorizon(option, null)}
                >
                  {simple ? horizonMeta[option].friendlyTab : horizonMeta[option].label}
                </TabsTrigger>
              ))}
            </TabsList>
          </Tabs>
        </div>

        {/* Below lg the ☰ sheet already holds both sheets, so the tab rail keeps the whole row
            instead of being squeezed until the fourth horizon is clipped. */}
        <div className="hidden items-center gap-2 lg:flex">
          <ThemeSwitch value={theme} onChange={onThemeChange} />
          <ViewModeSwitch value={viewMode} onChange={onViewModeChange} />
          <span className="type-caption hidden whitespace-nowrap text-fg-subtle xl:inline">
            {dates.length} day{dates.length === 1 ? '' : 's'} saved
          </span>
        </div>
      </div>

      {/* Everything the phone hides lives here, in one thumb-reachable sheet. */}
      <Dialog open={menuOpen} onOpenChange={setMenuOpen}>
        <DialogContent className="sm:max-w-md">
          <DialogStickyHeader>
            <div className="min-w-0">
              <DialogTitle className="type-title text-fg">Desk controls</DialogTitle>
              <DialogDescription className="type-caption mt-1 text-fg-subtle">
                Trading day, detail level, theme and every sheet — one tap away.
              </DialogDescription>
            </div>
            <DialogClose asChild>
              <Button variant="ghost" aria-label="Close desk controls" className="h-9 px-2.5">
                Close
              </Button>
            </DialogClose>
          </DialogStickyHeader>

          <div className="flex flex-col gap-4 pb-2">
            <div className="flex flex-col gap-1.5">
              <span className="type-eyebrow text-fg-subtle">
                Trading day
              </span>
              {datePicker()}
            </div>

            <div className="flex flex-wrap items-center gap-2">
              <ViewModeSwitch value={viewMode} onChange={onViewModeChange} />
              <ThemeSwitch value={theme} onChange={onThemeChange} />
            </div>

            <div className="grid grid-cols-2 gap-2">
              <Button variant="secondary" onClick={sheetAction(onOpenResearch)} disabled={busy !== null}>
                {simple ? 'Check a Stock' : 'Research'}
              </Button>
              <Button variant="secondary" onClick={sheetAction(onOpenScorecard)} disabled={busy !== null}>
                {simple ? 'Past Results' : 'Scorecard'}
              </Button>
              <Button variant="secondary" onClick={sheetAction(onOpenGuide)} disabled={busy !== null}>
                {simple ? 'How it works' : 'Guide'}
              </Button>
              <Button
                variant="secondary"
                onClick={sheetAction(onOpenShortcuts)}
                onPointerEnter={preloadKeyboardShortcutsDialog}
                disabled={busy !== null}
              >
                Keyboard shortcuts
              </Button>
              {simple ? null : (
                <Button
                  variant="secondary"
                  onClick={sheetAction(onOpenKnowledge)}
                  disabled={busy !== null}
                >
                  Knowledge base
                </Button>
              )}
            </div>

            {simple || !quotationImport ? null : (
              <div className="rounded-xl border border-line/70 bg-ink-850/60 p-3">
                <p className="mb-2 type-eyebrow text-fg-subtle">
                  Daily quotations
                </p>
                {quotationImport}
              </div>
            )}
          </div>
        </DialogContent>
      </Dialog>
    </header>
  );
}

