import { useCallback, useEffect, useState } from 'react';

/**
 * Two presentation layers over the exact same server payload:
 *
 * - `simple` (default): plain-English trade advice cards with per-card technical drawers.
 * - `pro`: the dense quant terminal (14-column table, macro tiles and RAG tools upfront).
 *
 * Nothing about the data changes between modes — only how much of it is shown at once.
 */
export type ViewMode = 'simple' | 'pro';

export const VIEW_MODE_STORAGE_KEY = 'psedesk.viewMode';
export const VIEW_MODE_ATTRIBUTE = 'data-view-mode';

/** Paints the Simple/Pro typography by flipping one attribute on `<html>`. */
export function applyViewMode(mode: ViewMode): void {
  if (typeof document === 'undefined') {
    return;
  }

  document.documentElement.setAttribute(VIEW_MODE_ATTRIBUTE, mode);
}

export function isViewMode(value: string | null | undefined): value is ViewMode {
  return value === 'simple' || value === 'pro';
}

/** Stored preference; anything unreadable or missing falls back to `simple`. */
export function readStoredViewMode(): ViewMode {
  if (typeof window === 'undefined') {
    return 'simple';
  }

  try {
    const stored = window.localStorage.getItem(VIEW_MODE_STORAGE_KEY);
    return isViewMode(stored) ? stored : 'simple';
  } catch {
    // Storage can be blocked (private browsing, disabled cookies); advise by default.
    return 'simple';
  }
}

export function storeViewMode(mode: ViewMode): void {
  try {
    window.localStorage.setItem(VIEW_MODE_STORAGE_KEY, mode);
  } catch {
    // A blocked storage quota must never break the terminal.
  }
}

/** Persisted `simple | pro` switch. Simple is the default for every new visitor. */
export function useViewMode(): [ViewMode, (mode: ViewMode) => void] {
  const [viewMode, setViewMode] = useState<ViewMode>(readStoredViewMode);

  useEffect(() => {
    applyViewMode(viewMode);
    storeViewMode(viewMode);
  }, [viewMode]);

  const update = useCallback((mode: ViewMode) => setViewMode(mode), []);

  return [viewMode, update];
}
