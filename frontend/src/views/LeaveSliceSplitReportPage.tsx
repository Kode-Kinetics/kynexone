'use client';

import { useEffect, useState } from 'react';
import { SectionHeader } from '@/src/components/SectionHeader';
import { payrollApi, type LeaveSliceSplitReport, type LeaveSliceSplitRow } from '@/src/api/payroll';
import { useT } from '@/src/hooks/useT';
import { useFormat } from '@/src/hooks/useFormat';
import { serverMessage } from '@/src/components/gl/glUi';

/**
 * Read-only list (spec M1-M3) of unpaid-leave deductions written as ONE row for a leave that spans several months, before
 * month slicing existed, with how each would split. Nothing here changes a row: the owner signs off before any data change.
 */
export function LeaveSliceSplitReportPage() {
  const t = useT();
  const fmt = useFormat();
  const [report, setReport] = useState<LeaveSliceSplitReport | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    payrollApi.leaveSliceSplitReport()
      .then(setReport)
      .catch(e => setError(serverMessage(e, t) || t('The report could not be loaded.')));
  }, [t]);

  const sections: { key: keyof Omit<LeaveSliceSplitReport, 'readOnly'>; title: string; blurb: string }[] = [
    { key: 'wouldSplit', title: 'Would be split', blurb: 'Still waiting for a payroll run. The amount would be shared across the months by days, adding up to the original exactly.' },
    { key: 'doNotReconcile', title: 'Do not reconcile', blurb: 'The month-by-month days do not match the deduction, or the leave is no longer approved. HR checks these; they are never split automatically.' },
    { key: 'alreadyDeducted', title: 'Already deducted', blurb: 'Taken in full by an earlier run in the first month. Left as they are: the total is right, only the timing differs.' },
    { key: 'sickScaleRows', title: 'Sick-leave pay-scale rows', blurb: 'Their days are unpaid-equivalent days, so they are not split by days. HR reviews them.' },
  ];

  return (
    <div className="space-y-6" data-testid="leave-split-report">
      <SectionHeader
        eyebrow={t('Payroll')}
        title={t('Multi-month leave deductions')}
        description={t('Read-only. Nothing on this page changes a deduction; any change waits for the owner’s sign-off.')}
      />
      {error && <p role="alert" className="text-sm text-rose-600 dark:text-rose-400">{error}</p>}
      {!report && !error && <p className="text-sm text-slate-600 dark:text-slate-400">{t('Loading…')}</p>}
      {report && sections.map(section => (
        <section key={section.key} className="rounded-xl border border-slate-200 bg-white p-4 dark:border-white/10 dark:bg-white/5">
          <h2 className="text-sm font-semibold text-slate-900 dark:text-white">
            {t(section.title)} <span className="tabular-nums">({report[section.key].length})</span>
          </h2>
          <p className="mt-1 text-xs leading-5 text-slate-600 dark:text-slate-400">{t(section.blurb)}</p>
          {report[section.key].length === 0
            ? <p className="mt-2 text-xs text-slate-500 dark:text-slate-400">{t('None.')}</p>
            : <ul className="mt-2 space-y-2">{report[section.key].map(row => <ReportRow key={row.impactId} row={row} money={n => fmt.money(n)} />)}</ul>}
        </section>
      ))}
    </div>
  );
}

function ReportRow({ row, money }: { row: LeaveSliceSplitRow; money: (n: number) => string }) {
  const t = useT();
  return (
    <li className="text-xs text-slate-800 dark:text-slate-200">
      <div className="flex flex-wrap gap-x-3">
        <span className="font-semibold">{`${row.employeeCode} — ${row.employeeName}`}</span>
        <span className="tabular-nums">{`${row.startDate} → ${row.endDate}`}</span>
        <span>{t('{days} day(s), {amount}, charged to {period}', { days: row.days, amount: money(row.amount), period: row.payPeriod })}</span>
      </div>
      {row.parts.length > 0 && (
        <ul className="mt-1 ps-4">
          {row.parts.map(p => (
            <li key={p.payPeriod} className="tabular-nums">
              {t('{period}: {days} day(s), {amount}', { period: p.payPeriod, days: p.days, amount: money(p.amount) })}
            </li>
          ))}
        </ul>
      )}
    </li>
  );
}
