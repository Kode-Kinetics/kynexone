'use client';

import { useState, useCallback } from 'react';
import { useAppToast } from '@/src/components/ui/AppToast';
import { useT } from './useT';
import { describeApiError } from '../lib/apiError';
import type { MessageParams } from '../i18n/translations';

/**
 * One translated sentence for a failed call — never the raw response body.
 * See lib/apiError.ts for the resolution order (error code → server message → HTTP status).
 */
export function parseApiError(err: unknown, t?: (key: string, params?: MessageParams) => string): string {
  return describeApiError(err, t);
}

/**
 * Wraps API calls with automatic error handling.
 * - `call(fn)` — surfaces error via toast (for user-initiated actions)
 * - `call(fn, { banner: true })` — sets `error` state for dismissible inline banner (for data loading)
 */
export function useApiCall() {
  const [error, setError] = useState<string | null>(null);
  const toast = useAppToast();
  const t = useT();

  const call = useCallback(
    async <T>(fn: () => Promise<T>, opts?: { banner?: boolean }): Promise<T | undefined> => {
      try {
        return await fn();
      } catch (e) {
        const msg = describeApiError(e, t);
        if (opts?.banner) {
          setError(msg);
        } else {
          toast.error(msg);
        }
        return undefined;
      }
    },
    [toast, t],
  );

  const clearError = useCallback(() => setError(null), []);

  return { error, clearError, call };
}
