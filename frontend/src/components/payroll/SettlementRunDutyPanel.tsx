'use client';

import Link from 'next/link';
import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { CalendarClock, ExternalLink } from 'lucide-react';
import { payrollApi, type FinalSettlementListRow } from '../../api/payroll';
import { useAuth } from '../../contexts/AuthContext';
import { useFormat } from '../../hooks/useFormat';
import { useT } from '../../hooks/useT';
import { describeApiError } from '../../lib/apiError';
import { isRunDutyOpen, settlementRunHref } from '../../lib/offboardingSettlement';
import { useConfirm } from '../ui/useConfirm';

/**
 * G-006: the Payroll Manager owns the off-cycle run that pays an approved final settlement. This lists every approved settlement
 * that has none yet, with the legal due date, and creates the run in one step (an OffCycle run for this settlement's company,
 * settling final settlements, holding only this employee, never paying recurring salary) before opening it. A settlement whose
 * run exists links to it. Amounts and approval are untouched.
 */
export function SettlementRunDutyPanel({ settlements, onChanged }: { settlements: FinalSettlementListRow[]; onChanged: () => void }) {
  const t = useT();
  const fmt = useFormat();
  const router = useRouter();
  const { hasPermission } = useAuth();
  const { confirm, dialog } = useConfirm();
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState('');
  const canCreate = hasPermission('payroll.write');

  const open = settlements.filter(isRunDutyOpen).sort((a, b) => a.settlementDueDate.localeCompare(b.settlementDueDate));
  const linked = settlements.filter(s => !!s.linkedRunId && (s.status === 'Approved' || s.status === 'Disbursing'));
  if (open.length === 0 && linked.length === 0) return null;

  const create = async (row: FinalSettlementListRow) => {
    setError('');
    const ok = await confirm({
      title: t('Create the off-cycle run?'),
      message: t('This creates an off-cycle payroll run, in the company of this settlement, that pays only {name}’s final settlement of {amount}. It pays no recurring salary. You then process, approve and lock it as usual.', {
        name: row.employeeName, amount: fmt.money(row.netPayable, row.currency),
      }),
      confirmLabel: t('Create off-cycle run'),
      tone: 'primary',
    });
    if (!ok) return;
    setBusy(row.id);
    try {
      const { runId } = await payrollApi.createFinalSettlementRun(row.id);
      onChanged();
      router.push(settlementRunHref(runId));
    } catch (e) {
      setError(describeApiError(e, t));
    } finally {
      setBusy(null);
    }
  };

  return (
    <section className="surface space-y-3 border-s-4 border-s-amber-500 p-4" data-testid="settlement-run-duty" aria-labelledby="settlement-run-duty-title">
      {dialog}
      <div>
        <h2 id="settlement-run-duty-title" className="flex items-center gap-2 text-sm font-semibold text-slate-900 dark:text-white">
          <CalendarClock className="h-4 w-4 text-amber-600" aria-hidden="true" />{t('Off-cycle runs to create')}
        </h2>
        <p className="mt-1 text-xs text-slate-500 dark:text-slate-400">{t('A leaver is paid through an off-cycle run. Each approved settlement below has none yet; the labour law sets the date it must be paid by.')}</p>
      </div>
      {error && <p role="alert" className="rounded-lg bg-rose-50 px-3 py-2 text-sm font-semibold text-rose-700 dark:bg-rose-500/10 dark:text-rose-200">{error}</p>}
      <ul className="divide-y divide-slate-100 dark:divide-white/5">
        {open.map(row => (
          <li key={row.id} className="flex flex-wrap items-center justify-between gap-3 py-2.5" data-testid={`run-duty-${row.id}`}>
            <div>
              <p className="text-sm font-medium text-slate-800 dark:text-slate-100">{row.employeeName} <span className="font-mono text-xs text-slate-500">{row.employeeCode}</span></p>
              <p className={`text-xs ${row.isOverdue ? 'font-semibold text-rose-600 dark:text-rose-400' : 'text-slate-500 dark:text-slate-400'}`}>
                {t('Due by {date}', { date: fmt.date(row.settlementDueDate) })}
                {row.isOverdue ? ` · ${t('{days}d overdue', { days: row.overdueDays })}` : ''}
                {' · '}{fmt.money(row.netPayable, row.currency)}
              </p>
            </div>
            {canCreate
              ? <button type="button" className="btn-primary" disabled={busy !== null} onClick={() => void create(row)}>{busy === row.id ? t('Creating…') : t('Create off-cycle run')}</button>
              : <span className="text-xs text-slate-500">{t('A Payroll Manager creates this run.')}</span>}
          </li>
        ))}
        {linked.map(row => (
          <li key={row.id} className="flex flex-wrap items-center justify-between gap-3 py-2.5" data-testid={`run-linked-${row.id}`}>
            <p className="text-sm text-slate-700 dark:text-slate-200">{row.employeeName} <span className="text-xs text-slate-500">· {t('Off-cycle run created')}</span></p>
            <Link href={settlementRunHref(row.linkedRunId as string)} className="inline-flex items-center gap-1 text-sm text-sapphire hover:underline dark:text-cyanAccent">
              {t('Open the off-cycle run')} <ExternalLink className="h-3 w-3 rtl:-scale-x-100" aria-hidden="true" />
            </Link>
          </li>
        ))}
      </ul>
    </section>
  );
}
