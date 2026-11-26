#!/usr/bin/env node
// "How to read this screen": three steps and one remembered dismissal. The copy is part of the
// interface, so a rewrite that loses an action fails here. Run with `npm test`.
import assert from 'node:assert/strict';
import test from 'node:test';
import {
  HOW_TO_READ_KEY,
  HOW_TO_READ_STEPS,
  dismissHowToRead,
  isHowToReadDismissed,
  resetHowToRead,
  type PreferenceStorage,
} from '../howToRead.ts';

function fakeStorage(initial: Record<string, string> = {}): PreferenceStorage & { entries: Map<string, string> } {
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

test('the strip teaches exactly the three actions the screen supports', () => {
  assert.deepEqual(
    HOW_TO_READ_STEPS.map((step) => step.id),
    ['read-call', 'open-signal', 'practise'],
  );

  for (const step of HOW_TO_READ_STEPS) {
    assert.ok(step.title.trim().length > 0, step.id);
    assert.ok(step.body.trim().length > 0, step.id);
  }

  const copy = HOW_TO_READ_STEPS.map((step) => `${step.title} ${step.body}`).join(' ');
  assert.match(copy, /call/i, 'step 1 is the market call above the list');
  assert.match(copy, /cut-loss/i, 'step 2 names a price the row actually prints');
  assert.match(copy, /My Trades/i, 'step 3 names the control by its on-screen label');
});

test('the strip never implies a real order is placed', () => {
  const copy = HOW_TO_READ_STEPS.map((step) => `${step.title} ${step.body}`).join(' ');

  assert.match(copy, /pretend wallet/i);
  assert.match(copy, /nothing is sent to a broker/i);
});

test('a fresh browser has not dismissed it, and dismissing is one stored flag', () => {
  const storage = fakeStorage();

  assert.equal(isHowToReadDismissed(storage), false);
  // No storage at all (SSR, blocked cookies) must show the help, never claim it was dismissed.
  assert.equal(isHowToReadDismissed(null), false);

  dismissHowToRead(storage);

  assert.equal(storage.getItem(HOW_TO_READ_KEY), '1');
  assert.equal(storage.entries.size, 1, 'one preference, nothing about the reader');
  assert.equal(isHowToReadDismissed(storage), true);
});

test('the footer link brings the strip back', () => {
  const storage = fakeStorage();

  dismissHowToRead(storage);
  resetHowToRead(storage);

  assert.equal(isHowToReadDismissed(storage), false);
});

test('a storage that throws is silent in both directions', () => {
  const throwing: PreferenceStorage = {
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

  assert.equal(isHowToReadDismissed(throwing), false);
  assert.doesNotThrow(() => dismissHowToRead(throwing));
  assert.doesNotThrow(() => resetHowToRead(throwing));
  assert.doesNotThrow(() => dismissHowToRead(null));
  assert.doesNotThrow(() => resetHowToRead(null));
});
