#!/usr/bin/env node
// First-run legal gate: the only thing the desk remembers about a reader is which disclosure
// version they accepted. Run with `npm test` (node:test, no network, no DOM).
import assert from 'node:assert/strict';
import test from 'node:test';
import {
  CURRENT_LEGAL_VERSION,
  LEGAL_CONSENT_KEY,
  LEGAL_SECTIONS,
  clearLegalAcceptance,
  hasAcceptedCurrentLegalVersion,
  recordLegalAcceptance,
  type ConsentStorage,
} from '../legalConsent.ts';

function fakeStorage(initial: Record<string, string> = {}): ConsentStorage & { entries: Map<string, string> } {
  const entries = new Map(Object.entries(initial));

  return {
    entries,
    getItem: (key) => entries.get(key) ?? null,
    setItem: (key, value) => {
      entries.set(key, value);
    },
    removeItem: (key) => {
      entries.delete(key);
    },
  };
}

test('a first run has not accepted the notice yet', () => {
  assert.equal(hasAcceptedCurrentLegalVersion(fakeStorage()), false);
  // No storage at all (SSR, or a browser that blocks it) must never count as consent.
  assert.equal(hasAcceptedCurrentLegalVersion(null), false);
});

test('accepting records exactly one versioned flag', () => {
  const storage = fakeStorage();

  assert.equal(recordLegalAcceptance(storage), true);
  assert.equal(storage.entries.size, 1);
  assert.equal(storage.getItem(LEGAL_CONSENT_KEY), CURRENT_LEGAL_VERSION);
  assert.equal(hasAcceptedCurrentLegalVersion(storage), true);
});

test('a notice older than the stored acceptance shows the gate again', () => {
  const storage = fakeStorage({ [LEGAL_CONSENT_KEY]: '2020-01-v0' });

  assert.equal(hasAcceptedCurrentLegalVersion(storage), false);
});

test('reviewing the notice clears the flag so the gate can show again', () => {
  const storage = fakeStorage();

  recordLegalAcceptance(storage);
  clearLegalAcceptance(storage);

  assert.equal(hasAcceptedCurrentLegalVersion(storage), false);
});

test('a storage that throws is treated as no consent, never as an error', () => {
  const throwing: ConsentStorage = {
    getItem: () => {
      throw new Error('SecurityError');
    },
    setItem: () => {
      throw new Error('QuotaExceededError');
    },
    removeItem: () => {
      throw new Error('SecurityError');
    },
  };

  assert.equal(hasAcceptedCurrentLegalVersion(throwing), false);
  assert.equal(recordLegalAcceptance(throwing), false);
  assert.doesNotThrow(() => clearLegalAcceptance(throwing));
  assert.doesNotThrow(() => clearLegalAcceptance(null));
});

test('the notice leads with a non-advice section and covers simulated results and data flow', () => {
  assert.ok(LEGAL_SECTIONS.length >= 4);

  const titles = LEGAL_SECTIONS.map((section) => section.title);
  assert.equal(titles[0], 'Not investment advice');
  assert.ok(titles.includes('Written for one reader'));
  assert.ok(titles.includes('Hypothetical and simulated results'));
  assert.ok(titles.includes('What leaves this machine'));
  assert.ok(titles.includes('Your data'));

  // The reader must be told the instruction-shaped copy is single-user, not a recommendation, and
  // that running someone else's money is the line that matters legally.
  const singleReader = LEGAL_SECTIONS.find((section) => section.title === 'Written for one reader');
  assert.ok(singleReader);
  assert.match(singleReader.body, /not a recommendation/i);
  assert.match(singleReader.body, /anyone else’s money/i);
  assert.match(singleReader.body, /Securities Regulation Code/i);

  for (const section of LEGAL_SECTIONS) {
    assert.ok(section.body.trim().length > 0, section.title);
  }

  // A disclosure that promises profits is worse than none.
  const joined = LEGAL_SECTIONS.map((section) => `${section.title} ${section.body}`).join(' ');
  assert.doesNotMatch(joined, /guarantee|sure profit|risk-free/i);
});
