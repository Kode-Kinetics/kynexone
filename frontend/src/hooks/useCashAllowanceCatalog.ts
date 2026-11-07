'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import { entitlementsApi, type EntitlementMatrix } from '../api/entitlements';
import { useAuth } from '../contexts/AuthContext';
import { useLocale } from '../contexts/LocaleContext';
import { allowanceChoices, gradeAllowanceLines, type AllowanceChoice, type AllowanceDraftLine } from '../lib/salaryAllowances';
import { useReleaseA } from '../lib/releaseA';

/**
 * The monthly cash allowances a salary can carry (the customer-defined ALW_* definitions) and what each grade gives, read from
 * Benefits by grade (GET /api/entitlements/matrix, entitlements.read).
 *  - `unavailable`: the tenant has no Benefits by grade, or this person may not read it. The section then only lists what is already
 *    on the salary, and says it cannot be changed here. Nothing is ever hidden or guessed.
 *  - `loading` / `error`: shown as such; a failed read never looks like "no allowances".
 */
export type CashAllowanceCatalog =
  | { status: 'unavailable' }
  | { status: 'loading' }
  | { status: 'error' }
  | { status: 'ready'; choices: AllowanceChoice[]; matrix: Pick<EntitlementMatrix, 'components' | 'cells'> };

const DATE = /^\d{4}-\d{2}-\d{2}$/;

export function useCashAllowanceCatalog(opts: { companyId?: string | null; asOf?: string | null; enabled?: boolean } = {}): { catalog: CashAllowanceCatalog; retry: () => void } {
  const { hasPermission } = useAuth();
  const { locale } = useLocale();
  const releaseA = useReleaseA();
  const allowed = (opts.enabled ?? true) && releaseA && hasPermission('entitlements.read');
  const companyId = opts.companyId || undefined;
  const asOf = opts.asOf && DATE.test(opts.asOf) ? opts.asOf : undefined;
  const [attempt, setAttempt] = useState(0);
  const [state, setState] = useState<{ status: 'loading' } | { status: 'error' } | { status: 'ready'; matrix: Pick<EntitlementMatrix, 'components' | 'cells'> }>({ status: 'loading' });

  useEffect(() => {
    if (!allowed) return;
    const controller = new AbortController();
    setState(s => (s.status === 'ready' ? s : { status: 'loading' }));
    entitlementsApi.matrix({ companyId, asOf }, controller.signal).then(
      matrix => { if (!controller.signal.aborted) setState(Array.isArray(matrix?.components) && Array.isArray(matrix?.cells) ? { status: 'ready', matrix } : { status: 'error' }); },
      () => { if (!controller.signal.aborted) setState({ status: 'error' }); },
    );
    return () => controller.abort();
  }, [allowed, companyId, asOf, attempt]);

  const catalog = useMemo<CashAllowanceCatalog>(() => {
    if (!allowed) return { status: 'unavailable' };
    if (state.status !== 'ready') return state;
    return { status: 'ready', matrix: state.matrix, choices: allowanceChoices(state.matrix.components, locale) };
  }, [allowed, state, locale]);

  return { catalog, retry: useCallback(() => setAttempt(n => n + 1), []) };
}

/** What the grade gives, as rows: [] when the grade is unknown or offers no cash allowance. */
export function gradeLinesFrom(catalog: CashAllowanceCatalog, gradeId: string | null | undefined): AllowanceDraftLine[] {
  return catalog.status === 'ready' && gradeId ? gradeAllowanceLines(catalog.matrix, gradeId) : [];
}
