import { useState, type MouseEvent, type ReactNode } from 'react';
import type { DateOption, Horizon } from '../types/signals';
import { cn } from '../lib/cn';
import { formatWeekday } from '../lib/formatters';
import { horizonMeta } from '../lib/horizons';
import { describeRunLabel } from '../lib/plainAdvice';
import {
  prefetchHorizon,
  preloadBeginnerGuideDialog,
  preloadKnowledgeBaseDialog,
  preloadPortfolioDialog,
  preloadResearchDialog,
  preloadScorecardDialog,
  preloadSignalTable,
} from '../lib/preloaders';
import type { ThemeMode } from '../lib/theme';
import type { ViewMode } from '../lib/viewMode';
import { Button } from './ui/Button';
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
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: SegmentOption<T>[];
  onChange: (value: T) => void;
}) {
  return (
    <div
      role="group"
      aria-label={label}
      className="inline-flex items-center gap-0.5 rounded-xl border border-line/80 bg-ink-900/80 p-1 sm:h-10"
    >
      {options.map((option) => (
        <button
          key={option.value}
          type="button"
          aria-pressed={value === option.value}
          onClick={() => onChange(option.value)}
          onPointerEnter={option.preload}
          onFocus={option.preload}
          className={cn(
            // `sm:h-full` makes the inner pill exactly fill the 40px frame, so segments never
            // float shorter than the buttons next to them. `ui-button` lifts the label to the
            // Simple-mode type floor (Pro keeps the 10px terminal scale).
            'inline-flex min-h-9 items-center rounded-lg px-3 text-[10px] font-semibold uppercase tracking-[0.14em] transition ui-button sm:h-full sm:min-h-0 sm:px-2.5',
            value === option.value
              ? 'bg-mint-400/10 text-mint-400 ring-1 ring-mint-400/30'
              : 'text-slate-500 hover:text-slate-200',
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
      value={value}
      onChange={onChange}
      options={[
        { value: 'simple', label: 'Simple' },
        { value: 'pro', label: 'Pro', preload: preloadSignalTable },
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
  onOpenScorecard,
  onOpenResearch,
  onOpenKnowledge,
  quotationImport,
}: {
  simple: boolean;
  busy: boolean;
  onOpenGuide: () => void;
  onOpenScorecard: () => void;
  onOpenResearch: () => void;
  onOpenKnowledge: () => void;
  quotationImport?: ReactNode;
}) {
  const closeThen = (action: () => void) => (event: MouseEvent<HTMLButtonElement>) => {
    event.currentTarget.closest('details')?.removeAttribute('open');
    action();
  };

  const itemClass =
    'flex w-full items-center justify-between gap-2 rounded-lg px-3 py-2 text-left text-xs font-medium text-slate-300 transition hover:bg-ink-700 hover:text-white disabled:cursor-not-allowed disabled:opacity-40';
  // Two tiny group labels replace a flat list of four unrelated sheets, so "what is this desk"
  // and "show me a result" stop reading as the same kind of thing.
  const groupClass =
    'px-3 pb-1 pt-2 text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-600';

  return (
    <details className="group relative">
      <summary className="flex h-10 cursor-pointer list-none items-center gap-1 rounded-xl border border-line/80 bg-ink-900/80 px-3 text-[11px] font-semibold uppercase tracking-[0.14em] text-slate-200 transition select-none ui-button hover:border-mint-400/40">
        More
        <span aria-hidden className="text-[10px] text-slate-500 transition group-open:rotate-180">
          ▾
        </span>
      </summary>
      <div className="absolute right-0 z-40 mt-2 w-60 rounded-xl border border-line bg-ink-850 p-1 shadow-2xl shadow-black/60">
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
      <span className="rounded-full bg-amber-400/20 px-1.5 text-[9px] font-semibold text-amber-400">
        {portfolioAlerts}
      </span>
    ) : null;

  return (
    <header className="panel flex flex-col gap-3 p-3 pt-[max(0.75rem,env(safe-area-inset-top))] sm:p-5 lg:gap-4">
      {/* Phone & small tablet: two thumb-sized rows instead of nine wrapped controls. */}
      <div className="flex flex-col gap-2 lg:hidden">
        <div className="flex items-center gap-2">
          <div className="flex size-9 shrink-0 items-center justify-center rounded-lg border border-mint-400/30 bg-mint-400/10 font-mono text-xs font-bold tracking-tight text-mint-400">
            PS
          </div>
          <div className="min-w-0">
            <h1 className="truncate text-base font-semibold tracking-tight text-white">
              PSE Signal Desk
            </h1>
            <p className="truncate text-[10px] text-slate-500">
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
        <div className="grid grid-cols-[minmax(0,1fr)_auto] gap-2">
          <Button
            variant="cta"
            busy={busy === 'run'}
            onClick={onRunNow}
            disabled={busy !== null}
            className="w-full whitespace-nowrap"
          >
            {busy === 'run' ? 'Running…' : runLabel}
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
          <div className="flex size-11 shrink-0 items-center justify-center rounded-xl border border-mint-400/30 bg-mint-400/10 font-mono text-sm font-bold tracking-tight text-mint-400">
            PS
          </div>
          <div className="min-w-0 max-w-md">
            <h1 className="text-lg font-semibold tracking-tight text-white">PSE Signal Desk</h1>
            <p className="truncate text-[11px] text-slate-500" title={tagline}>
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
            onPointerEnter={preloadPortfolioDialog}
            onFocus={preloadPortfolioDialog}
            disabled={busy !== null}
          >
            {simple ? 'My Trades' : 'Book'}
            {alertBadge}
          </Button>
          <Button variant="cta" busy={busy === 'run'} onClick={onRunNow} disabled={busy !== null}>
            {busy === 'run' ? 'Running…' : runLabel}
          </Button>
          <MoreMenu
            simple={simple}
            busy={busy !== null}
            onOpenGuide={onOpenGuide}
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
            <span className="hidden text-[10px] uppercase tracking-[0.16em] text-slate-500 xl:inline">
              How long do you want to hold?
            </span>
          ) : null}
          <Tabs
            value={horizon}
            onValueChange={(value) => onHorizonChange(value as Horizon)}
            className="min-w-0 max-w-full"
          >
            <TabsList>
              {horizonOrder.map((option) => (
                <TabsTrigger
                  key={option}
                  value={option}
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
          <span className="hidden whitespace-nowrap text-[11px] text-slate-500 xl:inline">
            {dates.length} day{dates.length === 1 ? '' : 's'} saved
          </span>
        </div>
      </div>

      {/* Everything the phone hides lives here, in one thumb-reachable sheet. */}
      <Dialog open={menuOpen} onOpenChange={setMenuOpen}>
        <DialogContent className="sm:max-w-md">
          <DialogStickyHeader>
            <div className="min-w-0">
              <DialogTitle className="text-sm font-semibold text-white">Desk controls</DialogTitle>
              <DialogDescription className="mt-0.5 text-[11px] text-slate-500">
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
              <span className="text-[10px] uppercase tracking-[0.16em] text-slate-500">
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
                <p className="mb-2 text-[10px] uppercase tracking-[0.16em] text-slate-500">
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

