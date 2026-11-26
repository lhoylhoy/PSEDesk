#!/usr/bin/env node
// The desk mark is the one drawing three surfaces share: the header badge, the browser tab icon
// and the iOS touch icon. The two icons are rasterised from `deskMarkGeometry`, so a candle that
// moves here moves everywhere — which is exactly why the drawing's constraints are pinned: three
// bars that step upwards, stay inside the 32-unit tile, never touch each other (at 16px a merged
// blob reads as a smudge) and carry exactly one full-strength candle as the focal point.
// Run with `npm test`.
import assert from 'node:assert/strict';
import test from 'node:test';
import {
  DESK_MARK_BODY_WIDTH,
  DESK_MARK_CANDLES,
  DESK_MARK_VIEWBOX,
  DESK_MARK_WICK_WIDTH,
} from '../deskMarkGeometry.ts';

test('the mark is three candles, so the tab icon never grows a fourth bar', () => {
  assert.equal(DESK_MARK_CANDLES.length, 3);
});

test('the candles ascend: each body closes above the one before it', () => {
  for (let index = 1; index < DESK_MARK_CANDLES.length; index += 1) {
    const previous = DESK_MARK_CANDLES[index - 1];
    const candle = DESK_MARK_CANDLES[index];

    assert.ok(candle.bodyTop < previous.bodyTop, `body ${index} does not sit higher`);
    assert.ok(candle.bodyBottom < previous.bodyBottom, `body ${index} does not close higher`);
    assert.ok(candle.wickTop < previous.wickTop, `wick ${index} does not sit higher`);
  }
});

test('every shape stays inside the tile and the glyph is centred', () => {
  for (const candle of DESK_MARK_CANDLES) {
    const left = candle.center - DESK_MARK_BODY_WIDTH / 2;
    const right = candle.center + DESK_MARK_BODY_WIDTH / 2;
    const wickLeft = candle.center - DESK_MARK_WICK_WIDTH / 2;
    const wickRight = candle.center + DESK_MARK_WICK_WIDTH / 2;

    assert.ok(left > 0 && right < DESK_MARK_VIEWBOX, 'a body overflows the tile');
    assert.ok(wickLeft > 0 && wickRight < DESK_MARK_VIEWBOX, 'a wick overflows the tile');
    assert.ok(candle.wickTop >= 0 && candle.wickBottom <= DESK_MARK_VIEWBOX, 'a wick is clipped');
    assert.ok(candle.wickTop < candle.bodyTop, 'a wick must overhang its body');
    assert.ok(candle.wickBottom > candle.bodyBottom, 'a wick must overhang its body');
  }

  const first = DESK_MARK_CANDLES[0];
  const last = DESK_MARK_CANDLES[DESK_MARK_CANDLES.length - 1];
  assert.equal(first.center + last.center, DESK_MARK_VIEWBOX, 'the trio is not centred in the tile');
});

test('neighbouring bodies never touch, even at a 16px favicon', () => {
  for (let index = 1; index < DESK_MARK_CANDLES.length; index += 1) {
    const previous = DESK_MARK_CANDLES[index - 1];
    const gap =
      DESK_MARK_CANDLES[index].center -
      DESK_MARK_BODY_WIDTH / 2 -
      (previous.center + DESK_MARK_BODY_WIDTH / 2);

    // 1 viewBox unit = 0.5px at 16px; anything smaller than 2 units renders as a single blob.
    assert.ok(gap >= 2, `the gap before body ${index} is ${gap} units`);
  }
});

test('the newest candle is the only full-strength one', () => {
  const opacities = DESK_MARK_CANDLES.map((candle) => candle.opacity);

  for (let index = 1; index < opacities.length; index += 1) {
    assert.ok(opacities[index] > opacities[index - 1], 'emphasis must grow towards the newest bar');
  }

  assert.equal(opacities[opacities.length - 1], 1, 'exactly one candle carries the signal');
  assert.ok(opacities.every((opacity) => opacity > 0 && opacity <= 1));
});
