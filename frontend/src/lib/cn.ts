export type ClassValue = string | false | null | undefined;

/** Minimal class-name joiner; the UI never needs conditional object syntax. */
export function cn(...values: ClassValue[]): string {
  return values.filter(Boolean).join(' ');
}
