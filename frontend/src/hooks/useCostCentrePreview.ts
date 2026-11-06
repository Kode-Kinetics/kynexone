'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import { employeesApi, type EmployeeCostCentrePreview } from '../api/employees';
import { previewFailure } from '../lib/employeeCostCentre';

export interface CostCentrePreviewParams { departmentId?: string; positionId?: string; costCenterId?: string }

export type CostCentrePreviewState =
  | { status: 'idle' }
  | { status: 'loading' }
  | { status: 'ready'; data: EmployeeCostCentrePreview }
  /** 429: the server asked us to wait; the hook asks again by itself after Retry-After. Not an error. */
  | { status: 'rateLimited'; retryAfterSeconds: number }
  | { status: 'error'; error: unknown };

/**
 * The server's resolution for a hypothetical org assignment (GET /api/employees/{id}/cost-centre/preview), so the screen never
 * guesses where the cost would land. `params` null = do not ask. Debounced (a fast run through the department list asks once),
 * and sequenced: each new question aborts the previous request, and an answer to an older question is dropped even if the
 * abort is not honoured, so the line on screen always answers the department currently in the form.
 */
export function useCostCentrePreview(employeeId: number, params: CostCentrePreviewParams | null, debounceMs = 250) {
  const key = params ? JSON.stringify([employeeId, params.departmentId ?? '', params.positionId ?? '', params.costCenterId ?? '']) : null;
  const [state, setState] = useState<CostCentrePreviewState>({ status: 'idle' });
  const [attempt, setAttempt] = useState(0);
  const sequence = useRef(0);

  useEffect(() => {
    const mine = ++sequence.current;
    if (!key) {
      setState({ status: 'idle' });
      return;
    }
    const [id, departmentId, positionId, costCenterId] = JSON.parse(key) as [number, string, string, string];
    setState({ status: 'loading' });
    const controller = new AbortController();
    let retryTimer: ReturnType<typeof setTimeout> | undefined;
    const timer = setTimeout(() => {
      employeesApi.costCentrePreview(id, { departmentId, positionId, costCenterId }, controller.signal).then(
        (data) => { if (mine === sequence.current) setState({ status: 'ready', data }); },
        (error) => {
          if (mine !== sequence.current || controller.signal.aborted) return;
          const failure = previewFailure(error);
          if (failure.kind === 'rateLimited') {
            setState({ status: 'rateLimited', retryAfterSeconds: failure.retryAfterSeconds });
            retryTimer = setTimeout(() => { if (mine === sequence.current) setAttempt((n) => n + 1); }, failure.retryAfterSeconds * 1000);
            return;
          }
          setState({ status: 'error', error });
        },
      );
    }, debounceMs);
    return () => {
      clearTimeout(timer);
      if (retryTimer) clearTimeout(retryTimer);
      controller.abort();
    };
  }, [key, attempt, debounceMs]);

  const retry = useCallback(() => setAttempt((n) => n + 1), []);
  return { state, retry };
}
