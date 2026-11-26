const compactPeso = new Intl.NumberFormat('en-PH', {
  notation: 'compact',
  compactDisplay: 'short',
  maximumFractionDigits: 2,
});

const integerFormatter = new Intl.NumberFormat('en-PH', { maximumFractionDigits: 0 });

const shortDate = new Intl.DateTimeFormat('en-PH', {
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  timeZone: 'Asia/Manila',
});

const weekdayDate = new Intl.DateTimeFormat('en-PH', {
  weekday: 'short',
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  timeZone: 'Asia/Manila',
});

const timestamp = new Intl.DateTimeFormat('en-PH', {
  day: '2-digit',
  month: 'short',
  year: 'numeric',
  hour: '2-digit',
  minute: '2-digit',
  hour12: false,
  timeZone: 'Asia/Manila',
});

const dash = '—';

function isNumber(value: number | null | undefined): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

/** A PSE price: four decimals below PHP 1 (tick-sensitive names), two above. */
export function formatPrice(value: number | null | undefined): string {
  if (!isNumber(value)) {
    return dash;
  }

  const digits = Math.abs(value) < 1 ? 4 : 2;

  return `₱${value.toLocaleString('en-PH', {
    minimumFractionDigits: digits,
    maximumFractionDigits: digits,
  })}`;
}

export function formatCompactPeso(value: number | null | undefined): string {
  if (!isNumber(value)) {
    return dash;
  }

  const sign = value < 0 ? '-' : '';
  return `${sign}₱${compactPeso.format(Math.abs(value))}`;
}

export function formatPercent(
  value: number | null | undefined,
  digits = 2,
  signed = true,
): string {
  if (!isNumber(value)) {
    return dash;
  }

  const sign = signed && value > 0 ? '+' : '';
  return `${sign}${value.toFixed(digits)}%`;
}

export function formatRatio(value: number | null | undefined): string {
  if (!isNumber(value)) {
    return dash;
  }

  return `${value.toFixed(2)} : 1`;
}

/**
 * Direction as a glyph, so a colour tone is never the only channel a figure carries.
 * A reader arriving from a Philippine broker app (where the up/down palette is often the
 * reverse of a Western terminal) — or with a colour-vision deficiency — still sees which way
 * a number moved. Zero and unreported values carry no glyph.
 */
export function directionGlyph(value: number | null | undefined): string {
  if (!isNumber(value) || value === 0) {
    return '';
  }

  return value > 0 ? '▲' : '▼';
}

/** A toned percentage with its direction glyph, e.g. `▼2.4%`, `0.0%`, `—`. */
export function formatDeltaPercent(value: number | null | undefined, digits = 1): string {
  if (!isNumber(value)) {
    return dash;
  }

  const glyph = directionGlyph(value);
  return glyph ? `${glyph}${Math.abs(value).toFixed(digits)}%` : `${(0).toFixed(digits)}%`;
}

/** The same convention in basis points, for the yield dial: `▲12.0bps`. */
export function formatDeltaBasisPoints(value: number | null | undefined, digits = 1): string {
  if (!isNumber(value)) {
    return dash;
  }

  const glyph = directionGlyph(value);
  return glyph ? `${glyph}${Math.abs(value).toFixed(digits)}bps` : `${(0).toFixed(digits)}bps`;
}

/**
 * Elapsed wall clock for a long server action — a full screen downloads a few hundred
 * candles before it ranks anything, so "still working, 42s" is the difference between a
 * patient reader and a reload.
 */
export function formatElapsedSeconds(totalSeconds: number): string {
  const seconds = Number.isFinite(totalSeconds) ? Math.max(0, Math.floor(totalSeconds)) : 0;
  const minutes = Math.floor(seconds / 60);
  const remainder = seconds % 60;

  return minutes > 0 ? `${minutes}m ${String(remainder).padStart(2, '0')}s` : `${remainder}s`;
}

export function formatInteger(value: number | null | undefined): string {
  if (!isNumber(value)) {
    return dash;
  }

  return integerFormatter.format(value);
}

function toManilaDate(value: string): Date {
  return new Date(`${value}T00:00:00+08:00`);
}

export function formatDate(value: string | null | undefined): string {
  if (!value) {
    return dash;
  }

  return shortDate.format(toManilaDate(value));
}

export function formatWeekday(value: string | null | undefined): string {
  if (!value) {
    return dash;
  }

  return weekdayDate.format(toManilaDate(value));
}

export function formatTimestamp(value: string | null | undefined): string {
  if (!value) {
    return dash;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? dash : timestamp.format(parsed);
}

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
