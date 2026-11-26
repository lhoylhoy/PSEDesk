/**
 * The desk mark, as data.
 *
 * "The desk tick" is three ascending PSE candlesticks: bodies plus wicks, no letters. It is the
 * one brand drawing in the project, and every surface reads it from here — the header badge
 * (`components/DeskLogo.tsx`), the tab icon (`public/favicon.svg` + `.ico`) and the iOS touch icon
 * are all generated from these numbers by `scripts/generate-icons.mjs`. That is why this module
 * has no imports at all: the React build, the icon generator and the `node:test` suite under
 * `lib/__tests__/` all load it directly.
 *
 * Coordinates are viewBox units on a 32×32 grid, so the mark is described once and scaled by the
 * consumer (a 16px tab icon and a 44px header badge are the same drawing).
 *
 * Design constraints, in order:
 * 1. Legible at 16px — three near-square bodies with a real gap, plus a wick thin enough to read as
 *    a shadow but overhanging far enough that the silhouette still says "candlestick" at tab size.
 * 2. One focal point — the newest (rightmost) candle is full strength, the two older ones fade.
 * 3. No text — a two-letter monogram says nothing about what the desk does and smears at 16px.
 */

export interface DeskMarkCandle {
  /** Horizontal centre of the body and its wick. */
  center: number;
  /** Top and bottom of the body (the open/close block). */
  bodyTop: number;
  bodyBottom: number;
  /** Full wick extent, top to bottom; it always overhangs the body on both sides. */
  wickTop: number;
  wickBottom: number;
  /**
   * Emphasis, 0–1. Applied as *group* opacity in every renderer (never as `fill-opacity`), so the
   * wick and the body of one candle composite as a single shape instead of darkening where they
   * overlap.
   */
  opacity: number;
}

/** A 32-unit grid keeps the geometry readable as fractions of the icon (72% band, 22% radius). */
export const DESK_MARK_VIEWBOX = 32;

/** Body block width. 6 units = 3px at a 16px favicon, with a 1.3px gap between neighbours. */
export const DESK_MARK_BODY_WIDTH = 6;

/** Wick stroke width. 1.6 units = 0.8px at 16px: a soft line at tab size, a thin shadow at 512px. */
export const DESK_MARK_WICK_WIDTH = 1.6;

/**
 * Corner radius of a candle body. Deliberately small: a pill-shaped body reads as a map pin or a
 * chess piece, while a near-square block reads as an open/close bracket at every size.
 */
export const DESK_MARK_BODY_RADIUS = 0.8;

/** Corner radius of the rounded-square tile (22% — the same proportion as `rounded-lg` at 36px). */
export const DESK_MARK_TILE_RADIUS = 7;

/**
 * Three candles, oldest first. Each wick is thin and peeks 3.5 units (more than half a body) past
 * its body on both sides — that overhang is the whole candlestick cue. A thick nub instead of a
 * thin line turns the trio into three batteries at large sizes, so the wick stays thin and accepts
 * being a soft hairline in a 16px tab.
 *
 * The trio fills ~72% of the tile on purpose: it is drawn bold once rather than given a separate
 * small-size cut that could drift away from this one.
 */
export const DESK_MARK_CANDLES: readonly DeskMarkCandle[] = [
  { center: 7.4, bodyTop: 18, bodyBottom: 24, wickTop: 14.5, wickBottom: 27.5, opacity: 0.62 },
  { center: 16, bodyTop: 12.4, bodyBottom: 18.4, wickTop: 8.9, wickBottom: 21.9, opacity: 0.8 },
  { center: 24.6, bodyTop: 6.8, bodyBottom: 12.8, wickTop: 3.3, wickBottom: 16.3, opacity: 1 },
];

/**
 * The two colours the raster icons are baked with. The app itself never uses these hex values —
 * the header badge draws the mark in `currentColor` over `bg-mint-400/10`, so it follows the
 * light/dark palette like every other figure. `tile` is `mint-400` at 10% over `ink-950`, i.e. the
 * opaque equivalent of the badge tint, so the tab icon and the badge are the same object.
 */
export const DESK_MARK_COLORS = {
  ink: '#05080d',
  mint: '#3ce6a8',
  tile: '#0a1e1c',
} as const;
