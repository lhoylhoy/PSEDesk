/**
 * First-run legal gate.
 *
 * The desk is a personal research tool: it publishes model output, never personalised
 * investment advice, and it stores exactly one thing about the reader — the legal version they
 * accepted — in `localStorage`. That flag is the minimum needed to avoid nagging on every reload
 * and it never leaves the browser, which is why it is spelled out in `PRIVACY.md`.
 *
 * Everything here is presentation copy plus one boolean; no consent is ever inferred from the
 * server and nothing is sent upstream when the reader accepts.
 */

export const LEGAL_CONSENT_KEY = 'psedesk.legalAcceptedVersion';

/**
 * Bump this string whenever `LEGAL_SECTIONS`, `DISCLAIMER.md` or `PRIVACY.md` change in a way a
 * reader should re-read; every browser then sees the gate once more.
 */
export const CURRENT_LEGAL_VERSION = '2026-11-v1';

export interface LegalSection {
  /** Card heading. */
  title: string;
  /** Two or three sentences a non-lawyer can read once. */
  body: string;
}

export const LEGAL_SECTIONS: LegalSection[] = [
  {
    title: 'Not investment advice',
    body:
      'PSE Signal Desk is a personal research and record-keeping tool. It is not an investment adviser, ' +
      'broker, dealer or fund manager, it is not registered with the SEC, and nothing it prints is a ' +
      'recommendation or a solicitation to buy or sell any security. Every figure is the output of rules ' +
      'and a language model working on public prices.',
  },
  {
    title: 'Your decisions, your money',
    body:
      'You are the only one who knows your income, debts, taxes and risk tolerance. Verify every price, ' +
      'board lot and filing against your broker and the PSE before acting, and never trade money you cannot ' +
      'afford to lose. Trading PSE-listed shares can lose more than the fees the desk models.',
  },
  {
    title: 'Hypothetical and simulated results',
    body:
      'Backtests, simulated CAGR, the paper portfolio and the model scorecards are walk-forward ' +
      'simulations over stored data. They are not the results of a real account, they exclude taxes and ' +
      'your broker’s own pricing, and a favourable past path does not repeat.',
  },
  {
    title: 'What leaves this machine',
    body:
      'Prices come from Yahoo Finance and PSE Edge under a personal, non-commercial research posture, which ' +
      'is why the API refuses non-loopback traffic by default. Signal research and uploaded documents are ' +
      'sent to the Ollama Cloud endpoint configured by the operator, so do not upload material you do not ' +
      'have the rights to index or cannot share with a third-party model provider.',
  },
  {
    title: 'Your data',
    body:
      'The desk has no user accounts, no analytics and no tracker cookies. The signal database and the paper ' +
      'portfolio live in local SQLite files on this machine, and your browser keeps only this acceptance flag, ' +
      'your view mode and your theme.',
  },
];

/** The storage surface this module needs, so a test can pass a fake. */
export type ConsentStorage = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>;

/**
 * True when this browser has already accepted the current legal version. A blocked or full
 * `localStorage` counts as "not accepted": the gate then shows again rather than silently
 * skipping the disclosure.
 */
export function hasAcceptedCurrentLegalVersion(storage: ConsentStorage | null = defaultStorage()): boolean {
  if (!storage) {
    return false;
  }

  try {
    return storage.getItem(LEGAL_CONSENT_KEY) === CURRENT_LEGAL_VERSION;
  } catch {
    return false;
  }
}

/** Records the acceptance, and reports whether the browser actually stored it. */
export function recordLegalAcceptance(
  storage: ConsentStorage | null = defaultStorage(),
  version: string = CURRENT_LEGAL_VERSION,
): boolean {
  if (!storage) {
    return false;
  }

  try {
    storage.setItem(LEGAL_CONSENT_KEY, version);
    return true;
  } catch {
    return false;
  }
}

/** Forgets the acceptance, so the gate shows again (used by the footer's "review the terms" link). */
export function clearLegalAcceptance(storage: ConsentStorage | null = defaultStorage()): void {
  if (!storage) {
    return;
  }

  try {
    storage.removeItem(LEGAL_CONSENT_KEY);
  } catch {
    // A browser that refuses storage also refuses to remember the flag; the gate shows again.
  }
}

function defaultStorage(): ConsentStorage | null {
  try {
    return typeof window === 'undefined' ? null : window.localStorage;
  } catch {
    // Safari in private mode throws on access, not on use.
    return null;
  }
}
