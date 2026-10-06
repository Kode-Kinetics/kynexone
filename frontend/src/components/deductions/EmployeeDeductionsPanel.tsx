'use client';

import { useLocale } from '../../contexts/LocaleContext';
import type { EmployeeDetail } from '../../api/employees';

/**
 * Deductions on the HR profile (Release A slice R3 owns this file): each payslip's deductions with category,
 * legal basis, balance and the Art. 93 cap. R0 mounted it in EmployeesPage behind the release_a flag; the slice
 * replaces this body.
 */
export function EmployeeDeductionsPanel({ employee }: { employee: EmployeeDetail }) {
  const { t } = useLocale();
  return (
    <div data-employee-id={employee.id} className="rounded-lg border border-dashed border-slate-300 p-4 text-sm text-slate-500 dark:border-white/15 dark:text-slate-400">
      {t('Deductions from each payslip, with their legal basis and the remaining balance, appear here.')}
    </div>
  );
}
