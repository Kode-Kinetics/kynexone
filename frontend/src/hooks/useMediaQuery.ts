'use client';

import { useSyncExternalStore } from 'react';

/**
 * SSR-safe media query. The server snapshot is `fallback` (desktop by default), so markup
 * that depends on it must also be correct — just less tailored — before hydration.
 */
export function useMediaQuery(query: string, fallback = false): boolean {
  return useSyncExternalStore(
    (onChange) => {
      if (typeof window === 'undefined' || !window.matchMedia) return () => {};
      const mql = window.matchMedia(query);
      mql.addEventListener('change', onChange);
      return () => mql.removeEventListener('change', onChange);
    },
    () => (typeof window !== 'undefined' && window.matchMedia ? window.matchMedia(query).matches : fallback),
    () => fallback,
  );
}
