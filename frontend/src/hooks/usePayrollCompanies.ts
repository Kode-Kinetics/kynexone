'use client';

import { useEffect, useState } from 'react';
import { payrollApi, type PayrollCompany } from '../api/payroll';
import type { CompaniesLoadState } from '../lib/payrollCurrency';

/**
 * The legal entities payroll amounts are denominated in (Company.DefaultCurrency), for
 * resolvePayrollRunCurrency / resolvePayrollRunsCurrency. One loader, shared by the payroll workspace
 * and the home dashboard, so both label an amount with the same company currency.
 *
 * `enabled: false` loads nothing and reports 'failed', so a caller that must not read payroll never
 * requests the list and never labels an amount.
 */
export function usePayrollCompanies(enabled = true) {
  const [companies, setCompanies] = useState<PayrollCompany[]>([]);
  const [state, setState] = useState<CompaniesLoadState>(enabled ? 'loading' : 'failed');
  const [attempt, setAttempt] = useState(0);
  useEffect(() => {
    if (!enabled) { setCompanies([]); setState('failed'); return; }
    let active = true;
    setState('loading');
    payrollApi.listCompanies()
      .then((items) => { if (active) { setCompanies(items); setState('loaded'); } })
      .catch(() => { if (active) { setCompanies([]); setState('failed'); } });
    return () => { active = false; };
  }, [attempt, enabled]);
  return { companies, state, retry: () => setAttempt((n) => n + 1) };
}
