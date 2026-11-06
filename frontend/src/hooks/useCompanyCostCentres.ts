'use client';

import { useCallback, useEffect, useState } from 'react';
import { costCentersApi, type CostCenterDto } from '../api/organization';

export type CompanyCostCentres =
  | { status: 'idle' | 'loading'; list: CostCenterDto[] }
  | { status: 'ready'; list: CostCenterDto[] }
  | { status: 'error'; list: CostCenterDto[]; error: unknown };

/**
 * One company's cost centres (every row the API returns for it, active or not, so a stored inactive one can still be named).
 * No company = nothing is asked and nothing is offered: never every company's list (D5).
 */
export function useCompanyCostCentres(companyId: string | null | undefined, enabled = true) {
  const [state, setState] = useState<CompanyCostCentres>({ status: 'idle', list: [] });
  const [attempt, setAttempt] = useState(0);

  useEffect(() => {
    if (!companyId || !enabled) {
      setState({ status: 'idle', list: [] });
      return;
    }
    let cancelled = false;
    setState({ status: 'loading', list: [] });
    costCentersApi.listAll(companyId).then(
      (list) => { if (!cancelled) setState({ status: 'ready', list: list.filter((c) => !c.companyId || c.companyId === companyId) }); },
      (error) => { if (!cancelled) setState({ status: 'error', list: [], error }); },
    );
    return () => { cancelled = true; };
  }, [companyId, enabled, attempt]);

  const reload = useCallback(() => setAttempt((n) => n + 1), []);
  return { ...state, reload };
}
