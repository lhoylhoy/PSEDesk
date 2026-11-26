#!/usr/bin/env node
// Number formatting is the terminal's most-used surface: every price, odds figure and macro
// delta goes through these helpers. These cases pin the two properties a reader depends on —
// a value always carries its unit, and an unreported value is never a zero — plus the
// direction glyph that keeps colour from being the only channel. Run with `npm test`.
import assert from 'node:assert/strict';
import test from 'node:test';
import {
  directionGlyph,
  formatCompactPeso,
  formatDeltaBasisPoints,
  formatDeltaPercent,
  formatElapsedSeconds,
  formatPercent,
  formatPrice,
  formatRatio,
} from '../formatters.ts';

const dash = '—';
const unreported: Array<number | null | undefined> = [null, undefined, Number.NaN];

test('a price always carries the peso sign and the PSE tick precision', () => {
  // Four decimals below ₱1 (tick-sensitive names), two above.
  assert.equal(formatPrice(0.5), '₱0.5000');
  assert.equal(formatPrice(0.0625), '₱0.0625');
  assert.equal(formatPrice(147.2), '₱147.20');
  assert.equal(formatPrice(1234.5), '₱1,234.50');
});

test('an unreported figure renders the dash, never a zero', () => {
  for (const value of unreported) {
    assert.equal(formatPrice(value), dash);
    assert.equal(formatCompactPeso(value), dash);
    assert.equal(formatPercent(value), dash);
    assert.equal(formatRatio(value), dash);
    assert.equal(formatDeltaPercent(value), dash);
    assert.equal(formatDeltaBasisPoints(value), dash);
  }
});

test('compact peso keeps its unit and the sign the server reported', () => {
  assert.equal(formatCompactPeso(1_250_000), '₱1.25M');
  assert.equal(formatCompactPeso(-1_250_000), '-₱1.25M');
  assert.equal(formatCompactPeso(72_828_984), '₱72.83M');
});

test('percent and ratio are never emitted as bare numbers', () => {
  assert.equal(formatPercent(12.34), '+12.34%');
  assert.equal(formatPercent(-3.1, 1), '-3.1%');
  assert.equal(formatPercent(0), '0.00%');
  assert.equal(formatPercent(48.4, 0, false), '48%');
  assert.equal(formatRatio(1.875), '1.88 : 1');
});

test('a toned delta carries a direction glyph, so colour is not the only channel', () => {
  assert.equal(directionGlyph(2.4), '▲');
  assert.equal(directionGlyph(-2.4), '▼');
  assert.equal(directionGlyph(0), '');
  assert.equal(directionGlyph(null), '');

  assert.equal(formatDeltaPercent(2.4), '▲2.4%');
  assert.equal(formatDeltaPercent(-2.4), '▼2.4%');
  assert.equal(formatDeltaPercent(0, 1), '0.0%');
  assert.equal(formatDeltaPercent(1.234, 2), '▲1.23%');

  assert.equal(formatDeltaBasisPoints(12.04), '▲12.0bps');
  assert.equal(formatDeltaBasisPoints(-3, 0), '▼3bps');
  assert.equal(formatDeltaBasisPoints(0), '0.0bps');
});

test('elapsed time is reported in a form a waiting reader can read', () => {
  assert.equal(formatElapsedSeconds(0), '0s');
  assert.equal(formatElapsedSeconds(42), '42s');
  assert.equal(formatElapsedSeconds(67), '1m 07s');
  assert.equal(formatElapsedSeconds(600), '10m 00s');
  // A clock that went backwards or never started must never print "NaNs".
  assert.equal(formatElapsedSeconds(-5), '0s');
  assert.equal(formatElapsedSeconds(Number.NaN), '0s');
});
