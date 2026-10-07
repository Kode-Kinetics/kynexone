'use client';

import { useEffect, useState } from 'react';
import { deductionsApi, type DeductionStatement } from '../../api/deductions';
import { useLocale } from '../../contexts/LocaleContext';
import { useReleaseA } from '../../lib/releaseA';
import { DeductionStatementView } from './DeductionStatementView';

/**
 * One payslip's deductions explained, for the employee (Release A slice R3): mounted once in "My Payslips" under the
 * payslip's lines. Renders nothing unless the tenant has release_a on, or when the statement is not available.
 */
export function PayslipDeductionsBreakdown({ payslipId }: { payslipId: string }) {
  const enabled = useReleaseA();
  if (!enabled) return null;
  return <Breakdown key={payslipId} payslipId={payslipId} />;
}

function Breakdown({ payslipId }: { payslipId: string }) {
  const { t } = useLocale();
  const [statement, setStatement] = useState<DeductionStatement | null>(null);
  useEffect(() => {
    let cancelled = false;
    deductionsApi.myPayslip(payslipId).then((s) => { if (!cancelled) setStatement(s); }).catch(() => { if (!cancelled) setStatement(null); });
    return () => { cancelled = true; };
  }, [payslipId]);
  if (!statement) return null;
  // A slip whose lines are missing is said so, never shown as an empty breakdown.
  if (statement.lines.length === 0 || !statement.reconciles)
    return (
      <p role="status" data-testid="payslip-deductions-breakdown" className="rounded-xl bg-amber-50 p-3 text-sm font-medium text-amber-900 dark:bg-amber-500/10 dark:text-amber-200">
        {t("This payslip can't be broken down yet. Ask HR.")}
      </p>
    );
  return (
    <details className="rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="payslip-deductions-breakdown">
      <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Why each deduction is made')}</summary>
      <div className="mt-3">
        <DeductionStatementView statement={statement} audience="employee" showHeader={false} />
      </div>
    </details>
  );
}
