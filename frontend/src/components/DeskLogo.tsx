import { cn } from '../lib/cn';
import {
  DESK_MARK_BODY_RADIUS,
  DESK_MARK_BODY_WIDTH,
  DESK_MARK_CANDLES,
  DESK_MARK_VIEWBOX,
  DESK_MARK_WICK_WIDTH,
} from '../lib/deskMarkGeometry';

/**
 * The desk mark: three ascending candlesticks, drawn in `currentColor` so it inherits the palette
 * (mint on the tinted tile in both light and dark themes).
 *
 * It is decorative — the header's `<h1>` carries the name — so it is hidden from assistive tech
 * instead of announcing "image" next to a heading that already says "PSE Signal Desk".
 */
export function DeskMark({ className }: { className?: string }) {
  return (
    <svg
      aria-hidden="true"
      viewBox={`0 0 ${DESK_MARK_VIEWBOX} ${DESK_MARK_VIEWBOX}`}
      className={className}
    >
      {DESK_MARK_CANDLES.map((candle) => (
        // `opacity` (not `fill-opacity`) is what keeps the wick from double-darkening the body
        // where the two rects overlap — the pair composites as one candle.
        <g key={candle.center} fill="currentColor" opacity={candle.opacity}>
          <rect
            x={candle.center - DESK_MARK_WICK_WIDTH / 2}
            y={candle.wickTop}
            width={DESK_MARK_WICK_WIDTH}
            height={candle.wickBottom - candle.wickTop}
            rx={DESK_MARK_WICK_WIDTH / 2}
          />
          <rect
            x={candle.center - DESK_MARK_BODY_WIDTH / 2}
            y={candle.bodyTop}
            width={DESK_MARK_BODY_WIDTH}
            height={candle.bodyBottom - candle.bodyTop}
            rx={DESK_MARK_BODY_RADIUS}
          />
        </g>
      ))}
    </svg>
  );
}

/** Badge sizes that already existed in the header: the phone row and the desktop command bar. */
const badgeSizes = {
  sm: 'size-9 rounded-lg',
  md: 'size-11 rounded-xl',
} as const;

/**
 * The header logo: the mark inside the tinted badge the terminal has always used (`mint-400/10`
 * fill, `mint-400/30` ring). Only the size and radius differ between the two call sites, so they
 * are the only thing the caller gets to choose.
 */
export function DeskLogo({ size = 'md' }: { size?: keyof typeof badgeSizes }) {
  return (
    <span
      aria-hidden="true"
      className={cn(
        'flex shrink-0 items-center justify-center border border-mint-400/30 bg-mint-400/10 text-mint-400',
        badgeSizes[size],
      )}
    >
      {/* The geometry already insets the glyph to ~72% of the tile, exactly like the favicon. */}
      <DeskMark className="size-full" />
    </span>
  );
}
