'use client';

import { useEffect, useState } from 'react';
import { payrollApi, type SalaryStructure } from '../api/payroll';
import { requireList } from '../lib/listResponse';
import { salaryStructureChoices } from '../lib/salaryStructureChoices';

/**
 * Whether the employee's current salary structure is no longer valid for them (retired, superseded or another company's).
 * The server refuses a salary package on such a structure, and HR no longer picks one, so the edit then sends the
 * package on the grade's own structure instead. False while loading, or when the list could not be read (the server decides).
 */
export function useStaleSalaryStructure(companyId: string | null | undefined, currentCode: string | null | undefined, enabled: boolean): boolean {
  const [stale, setStale] = useState(false);
  const code = currentCode?.trim() || '';
  useEffect(() => {
    setStale(false);
    if (!enabled || !code) return;
    let cancelled = false;
    payrollApi.listSalaryStructures(companyId || undefined)
      .then((rows) => {
        if (cancelled) return;
        const choices = salaryStructureChoices(requireList<SalaryStructure>(rows, 'salary structures'), companyId, code);
        setStale(!!choices.find((choice) => choice.value === code)?.retainedCurrent);
      })
      .catch(() => { /* unknown: leave it to the server */ });
    return () => { cancelled = true; };
  }, [companyId, code, enabled]);
  return stale;
}
