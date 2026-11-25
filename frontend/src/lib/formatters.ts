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
