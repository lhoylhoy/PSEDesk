import { useCallback, useEffect, useState } from 'react';

/**
 * Colour theme for the terminal. Both themes paint the same components: the palette lives in
 * CSS custom properties on `:root[data-theme=…]`, so no component needs a theme prop and the
 * server payload never changes. Light is the default for a first visit on a light system —
 * a consumer finance reader should not be dropped into a dark trading terminal.
 */
export type ThemeMode = 'light' | 'dark';

export const THEME_STORAGE_KEY = 'psedesk.theme';
export const THEME_ATTRIBUTE = 'data-theme';

/** Browser chrome colours, matched to `ink-950` in both palettes. */
const THEME_COLORS: Record<ThemeMode, string> = {
  light: '#f3f6fb',
  dark: '#05080d',
};

export function isThemeMode(value: string | null | undefined): value is ThemeMode {
  return value === 'light' || value === 'dark';
}

/** What the operating system asks for; light when the browser cannot say. */
export function preferredTheme(): ThemeMode {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') {
    return 'light';
  }

  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

/** Stored choice first, then the system preference, then light. */
export function readStoredTheme(): ThemeMode {
  if (typeof window === 'undefined') {
    return 'light';
  }

  try {
    const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
    if (isThemeMode(stored)) {
      return stored;
    }
  } catch {
    // Blocked storage (private browsing) falls through to the system preference.
  }

  return preferredTheme();
}

export function storeTheme(theme: ThemeMode): void {
  if (typeof window === 'undefined') {
    return;
  }

  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, theme);
  } catch {
    // A blocked storage quota must never break the terminal.
  }
}

/** Paints the palette by flipping one attribute on `<html>`. */
export function applyTheme(theme: ThemeMode): void {
  if (typeof document === 'undefined') {
    return;
  }

  document.documentElement.setAttribute(THEME_ATTRIBUTE, theme);

  // Mobile browsers tint their chrome from this meta tag; keep it in step with the palette
  // so the status bar and address bar do not flash the other theme's colour.
  const themeColor = document.querySelector('meta[name="theme-color"]');
  if (themeColor) {
    themeColor.setAttribute('content', THEME_COLORS[theme]);
  }
}

/** Persisted light/dark switch. */
export function useTheme(): [ThemeMode, (theme: ThemeMode) => void] {
  const [theme, setTheme] = useState<ThemeMode>(readStoredTheme);

  useEffect(() => {
    applyTheme(theme);
    storeTheme(theme);
  }, [theme]);

  const update = useCallback((next: ThemeMode) => setTheme(next), []);

  return [theme, update];
}
