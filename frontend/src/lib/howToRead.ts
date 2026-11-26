/**
 * "How to read this screen" — the three-step path a first-time reader needs, and the one
 * preference that remembers they have seen it.
 *
 * This is deliberately separate from `legalConsent` (a gate) and from `WelcomeSetupBanner`
 * (account setup): it explains the screen they are looking at, it never blocks anything, and
 * clearing the storage key brings it back. Copy lives here so the component stays markup-only
 * and `npm test` can pin the words that teach the interface.
 */

export const HOW_TO_READ_KEY = 'psedesk.howToReadDismissed';

export type HowToReadStepId = 'read-call' | 'open-signal' | 'practise';

export interface HowToReadStep {
  id: HowToReadStepId;
  /** A verb phrase — the thing the reader does next. */
  title: string;
  /** One sentence saying what happens when they do it. */
  body: string;
}

export const HOW_TO_READ_STEPS: HowToReadStep[] = [
  {
    id: 'read-call',
    title: 'Read the call',
    body: 'The bar above says what kind of day it is: ride the wave, be selective, or stay in cash.',
  },
  {
    id: 'open-signal',
    title: 'Open one stock',
    body: 'Each row is one idea. Open it for the buy price, the target, the cut-loss and how many shares.',
  },
  {
    id: 'practise',
    title: 'Practise the trade',
    body: 'Log it in My Trades to track it in a pretend wallet. Nothing is sent to a broker.',
  },
];

/** The storage surface this module needs, so a test can pass a fake. */
export type PreferenceStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

/** True once the reader has hidden the strip. A browser that blocks storage just shows it again. */
export function isHowToReadDismissed(storage: PreferenceStorage | null = defaultStorage()): boolean {
  if (!storage) {
    return false;
  }

  try {
    return storage.getItem(HOW_TO_READ_KEY) === '1';
  } catch {
    return false;
  }
}

/** Remembers the dismissal, so the strip does not reappear on the next visit. */
export function dismissHowToRead(storage: PreferenceStorage | null = defaultStorage()): void {
  if (!storage) {
    return;
  }

  try {
    storage.setItem(HOW_TO_READ_KEY, '1');
  } catch {
    // A blocked quota only means the strip comes back next visit — never an error on screen.
  }
}

/** Brings the strip back (the footer's "How to read this screen" link). */
export function resetHowToRead(storage: PreferenceStorage | null = defaultStorage()): void {
  if (!storage) {
    return;
  }

  try {
    storage.removeItem(HOW_TO_READ_KEY);
  } catch {
    // Same contract as above: storage failures are silent.
  }
}

function defaultStorage(): PreferenceStorage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    // Safari in private mode throws on access, not on use.
    return null;
  }
}
