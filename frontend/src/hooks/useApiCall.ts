'use client';

import { useState, useCallback } from 'react';
import { useAppToast } from '@/src/components/ui/AppToast';

export function parseApiError(err: unknown): string {
  const ax = err as { response?: { status?: number; data?: unknown } };
  if (ax?.response?.data) {
    const d = ax.response.data as Record<string, unknown>;
    if (typeof d === 'string') return d;
    if (d.message) return String(d.message);
    if (d.title) return String(d.title);
    return `${ax.response.status}: ${JSON.stringify(d)}`;
  }
  if (err instanceof Error) return err.message;
  return String(err);
}

/**
 * Wraps API calls with automatic error handling.
 * - `call(fn)` — surfaces error via toast (for user-initiated actions)
 * - `call(fn, { banner: true })` — sets `error` state for dismissible inline banner (for data loading)
 */
export function useApiCall() {
  const [error, setError] = useState<string | null>(null);
  const toast = useAppToast();

  const call = useCallback(
    async <T>(fn: () => Promise<T>, opts?: { banner?: boolean }): Promise<T | undefined> => {
      try {
        return await fn();
      } catch (e) {
        const msg = parseApiError(e);
        if (opts?.banner) {
          setError(msg);
        } else {
          toast.error(msg);
        }
        return undefined;
      }
    },
    [toast],
  );

  const clearError = useCallback(() => setError(null), []);

  return { error, clearError, call };
}
