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
  if (!statement || statement.lines.length === 0) return null;
  return (
    <details className="rounded-xl border border-slate-200 p-3 dark:border-white/10" data-testid="payslip-deductions-breakdown">
      <summary className="cursor-pointer text-sm font-semibold text-slate-800 dark:text-slate-100">{t('Why each deduction is made')}</summary>
      <div className="mt-3">
        <DeductionStatementView statement={statement} audience="employee" showHeader={false} />
      </div>
    </details>
  );
}
