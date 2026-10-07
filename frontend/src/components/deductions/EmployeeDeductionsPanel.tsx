'use client';

import { useCallback, useEffect, useState } from 'react';
import { deductionsApi, type EmployeeDeductions } from '../../api/deductions';
import { useLocale } from '../../contexts/LocaleContext';
import type { EmployeeDetail } from '../../api/employees';
import { DeductionsHistory } from './DeductionsHistory';

const MONTHS = 6;

/**
 * Deductions on the HR profile (Release A slice R3): each recent payslip's deductions with category, legal basis, the
 * Art. 93 limit and the balance left, plus the employee's open loans and advances. Mounted by EmployeesPage behind the
 * release_a flag; the API enforces payroll.read and the caller's company and data scope.
 */
export function EmployeeDeductionsPanel({ employee }: { employee: EmployeeDetail }) {
  const { t } = useLocale();
  const [data, setData] = useState<EmployeeDeductions | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(() => {
    setError(null);
    setData(null);
    deductionsApi.employee(employee.id, MONTHS)
      .then(setData)
      .catch((e) => {
        const status = (e as { response?: { status?: number } })?.response?.status;
        setError(status === 403 ? t('You do not have access to payroll deductions.') : t('Deductions could not be loaded.'));
      });
  }, [employee.id, t]);
  useEffect(() => { load(); }, [load]);

  return (
    <div className="space-y-3" data-employee-id={employee.id} data-testid="employee-deductions-panel">
      <p className="text-xs text-slate-500 dark:text-slate-400">
        {t('Deductions from each payslip, with their legal basis and the remaining balance, appear here.')}
      </p>
      {error ? (
        <div role="alert" className="flex items-center justify-between gap-3 rounded-xl bg-amber-50 p-3 text-xs text-amber-800 dark:bg-amber-500/10 dark:text-amber-200">
          <span>{error}</span>
          <button type="button" onClick={load} className="font-semibold underline">{t('Retry')}</button>
        </div>
      ) : data === null ? (
        <div className="h-32 animate-pulse rounded-2xl bg-slate-100 dark:bg-white/[0.04]" aria-busy="true" />
      ) : (
        <DeductionsHistory data={data} audience="hr" months={MONTHS} />
      )}
    </div>
  );
}
