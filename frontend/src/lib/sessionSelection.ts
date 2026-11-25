import type { DateOption } from '../types/signals';

/**
 * Which stored session the terminal is reading.
 *
 * The desk can only re-run days it already has, so "latest" is the newest stored session —
 * never the wall-clock date and never whatever day happens to be selected. These helpers
 * keep that distinction in one place: the header label, the amber history strip and the
 * primary button all read the same decision instead of re-deriving it.
 */

/** The newest stored session date, or `null` before the first run. */
export function newestSessionDate(dates: readonly Pick<DateOption, 'date'>[]): string | null {
  return dates.reduce<string | null>(
    (newest, option) => (newest == null || option.date > newest ? option.date : newest),
    null,
  );
}

/**
 * True when the reader has stepped back to a stored session older than the newest one.
 * With no stored sessions there is nothing to be historical about, so it stays `false`.
 */
export function isViewingHistory(selectedDate: string | null, newest: string | null): boolean {
  return selectedDate != null && newest != null && selectedDate !== newest;
}
