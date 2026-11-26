/**
 * Keyboard shortcuts — the desk's second way in.
 *
 * The terminal is a reading surface, so every action already has a visible button. These keys
 * are an accelerator for a reader who lives on the keyboard, never a replacement: `SHORTCUT_GROUPS`
 * is the one list the shortcut sheet, the guide and the header hints all render, so a key can
 * never exist in the help sheet and not in the handler (or the reverse).
 *
 * Everything in this file is pure and DOM-free, which is what lets `npm test` (node:test over
 * `src/lib`) pin the mapping without a browser.
 */

/** Every action a key can fire. Values are stable ids, not labels. */
export type ShortcutActionId =
  | 'horizon-day'
  | 'horizon-week'
  | 'horizon-month'
  | 'horizon-year'
  | 'run'
  | 'research'
  | 'trades'
  | 'scorecard'
  | 'view-simple'
  | 'view-pro'
  | 'toggle-theme'
  | 'guide'
  | 'help';

/** The subset of `KeyboardEvent` the resolver reads, so a test can pass a plain object. */
export interface ShortcutKeyInput {
  key: string;
  altKey?: boolean;
  ctrlKey?: boolean;
  metaKey?: boolean;
  shiftKey?: boolean;
}

/** Where the key was pressed. Both flags suppress accelerators rather than swallow the keystroke. */
export interface ShortcutContext {
  /** The event target is a text field: the reader is typing, not navigating. */
  typing?: boolean;
  /** A modal/sheet owns the screen: only the shortcut list itself may open over it. */
  dialogOpen?: boolean;
}

export interface ShortcutItem {
  /** Keycaps as they should be drawn, e.g. `['?']` or `['Esc']`. */
  keys: string[];
  /** What the key does, in the reader's words. */
  label: string;
  /** The handler this row documents; omitted for keys the browser/dialog owns (Esc). */
  action?: ShortcutActionId;
}

export interface ShortcutGroup {
  title: string;
  items: ShortcutItem[];
}

/**
 * The single source of truth for the help sheet, the guide's shortcut section and the
 * `aria-keyshortcuts` attributes. Grouped by the question a reader is asking: where am I,
 * what can I do, how do I change what I see, and where is the help.
 */
export const SHORTCUT_GROUPS: ShortcutGroup[] = [
  {
    title: 'Move',
    items: [
      { keys: ['1'], label: 'Quick — hold for today', action: 'horizon-day' },
      { keys: ['2'], label: 'Short-term — 1 to 3 weeks', action: 'horizon-week' },
      { keys: ['3'], label: 'Medium-term — 1 to 3 months', action: 'horizon-month' },
      { keys: ['4'], label: 'Long-term — a year or more', action: 'horizon-year' },
    ],
  },
  {
    title: 'Act',
    items: [
      { keys: ['R'], label: 'Run the screen for the day on show', action: 'run' },
      { keys: ['K'], label: 'Check a stock', action: 'research' },
      { keys: ['T'], label: 'My Trades (the paper account)', action: 'trades' },
      { keys: ['D'], label: 'Past results', action: 'scorecard' },
    ],
  },
  {
    title: 'Change what you see',
    items: [
      { keys: ['S'], label: 'Simple — the everyday list', action: 'view-simple' },
      { keys: ['P'], label: 'Pro — the full quant table', action: 'view-pro' },
      { keys: ['L'], label: 'Light / dark', action: 'toggle-theme' },
    ],
  },
  {
    title: 'Help',
    items: [
      { keys: ['G'], label: 'How it works', action: 'guide' },
      { keys: ['?'], label: 'This list of shortcuts', action: 'help' },
      // Radix's dialog primitive owns Escape; it is listed so a reader can find it, not wired here.
      { keys: ['Esc'], label: 'Close a sheet' },
    ],
  },
];


/** Lower-case single characters only; `1`–`4` and `?` are the ones that matter. */
const KEY_ACTIONS: Record<string, ShortcutActionId> = {
  '1': 'horizon-day',
  '2': 'horizon-week',
  '3': 'horizon-month',
  '4': 'horizon-year',
  r: 'run',
  k: 'research',
  t: 'trades',
  d: 'scorecard',
  s: 'view-simple',
  p: 'view-pro',
  l: 'toggle-theme',
  g: 'guide',
  '?': 'help',
};

/**
 * Maps one keystroke to one action, or `null` when the terminal should keep its hands off.
 *
 * Rules, in order:
 * 1. A modifier means the reader is talking to the browser (⌘R, Ctrl+C, Alt+←) — never hijack it.
 * 2. A focused text field means they are typing a ticker or an amount — never steal the letter.
 * 3. A sheet is open: only `?` still resolves, so the shortcut list can be summoned or dismissed.
 * 4. Anything that is not a single printable character (arrows, Tab, Escape, F-keys) is left alone.
 */
export function resolveShortcut(
  input: ShortcutKeyInput,
  context: ShortcutContext = {},
): ShortcutActionId | null {
  if (input.ctrlKey || input.metaKey || input.altKey) {
    return null;
  }

  if (context.typing) {
    return null;
  }

  const key = normalizeKey(input.key);

  if (key === null) {
    return null;
  }

  if (context.dialogOpen) {
    return key === '?' ? 'help' : null;
  }

  return KEY_ACTIONS[key] ?? null;
}

function normalizeKey(key: string): string | null {
  if (key === '?') {
    return '?';
  }

  return key.length === 1 ? key.toLowerCase() : null;
}

/**
 * True when the event target is somewhere a reader types or is mid-choice. Kept DOM-optional
 * (`tagName` / `isContentEditable` / `role` are read structurally) so it is unit-testable with a
 * plain object.
 *
 * The `role` cases matter as much as the tags: an open Radix picker (the trading-day `Select`) puts
 * focus on a `[role="listbox"]`, and a letter there is its typeahead — the same hands-off rule as a
 * text field, or `R` would jump the list *and* start a screen run at the same time.
 */
export function isTypingTarget(target: EventTarget | null | undefined): boolean {
  if (!target || typeof target !== 'object') {
    return false;
  }

  const element = target as {
    tagName?: string;
    isContentEditable?: boolean;
    role?: string | null;
    ariaExpanded?: string | null;
  };

  if (element.isContentEditable) {
    return true;
  }

  const role = element.role ?? undefined;

  if (role === 'listbox' || role === 'option' || role === 'menu' || role === 'menuitem') {
    return true;
  }

  // A closed picker trigger is just a button — only an *open* one owns the keyboard.
  if (role === 'combobox') {
    return element.ariaExpanded === 'true';
  }

  const tag = element.tagName?.toUpperCase();
  return tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
}

/**
 * The keys documented for an action, e.g. `['R']` for `run`. The header buttons read this so a
 * label and its `aria-keyshortcuts` value can never disagree with the sheet.
 */
export function keysForAction(action: ShortcutActionId): string[] {
  for (const group of SHORTCUT_GROUPS) {
    for (const item of group.items) {
      if (item.action === action) {
        return item.keys;
      }
    }
  }

  return [];
}
