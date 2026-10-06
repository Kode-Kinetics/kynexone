'use client';

import { useLocale } from '../../contexts/LocaleContext';
import type { EmployeeDetail } from '../../api/employees';

/**
 * The employee's package on the HR profile (Release A slice R2 owns this file): pay in the Qiwa contract,
 * contract benefits fixed for the term, and facilities by current policy. R0 mounted it in EmployeesPage behind
 * the release_a flag; the slice replaces this body.
 */
export function EmployeePackagePanel({ employee }: { employee: EmployeeDetail }) {
  const { t } = useLocale();
  return (
    <div data-employee-id={employee.id} className="rounded-lg border border-dashed border-slate-300 p-4 text-sm text-slate-500 dark:border-white/15 dark:text-slate-400">
      {t('The employee’s package for the current contract year appears here.')}
    </div>
  );
}
