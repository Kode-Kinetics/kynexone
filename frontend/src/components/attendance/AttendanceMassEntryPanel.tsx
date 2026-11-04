'use client';

import { useId, useState } from 'react';
import { Users } from 'lucide-react';
import { attendanceApi } from '../../api/attendance';
import { useLocale } from '../../contexts/LocaleContext';
import { useFormat } from '../../hooks/useFormat';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { outcomeTone } from '../../lib/attendanceBulkResult';
import { describeBulkError } from '../../lib/attendanceBulkError';
import {
  MASS_ENTRY_MAX_DAYS,
  MASS_ENTRY_MAX_EMPLOYEES,
  MASS_ENTRY_MAX_PUNCHES,
  MASS_ENTRY_PUNCH_MATHS_KEY,
  MASS_ENTRY_MAX_REASON,
  MASS_ENTRY_SUBMIT_KEY,
  MASS_ENTRY_SUMMARY_KEY,
  massEntryState,
  normaliseBulkEntryResult,
  tenantToday,
  type BulkEntryResultShape,
} from '../../lib/attendanceBulkEntry';
import { selectedIds, type Selection } from '../../lib/attendanceSelection';
import { StatusChip } from '../StatusChip';
import { EmployeeMultiSelect } from './EmployeeMultiSelect';
import { SectionTitle } from './attendanceUi';

/**
 * Mass entry (attendance.write): the same in time, and optionally out time, for chosen employees over a date
 * range, with a required reason. The server refuses per employee-day (locked payroll period, future time,
 * the caller's own record, unknown or inactive employee) and reports each refusal; nothing is guessed here.
 */
export function AttendanceMassEntryPanel({ canWrite, onRecorded }: { canWrite: boolean; onRecorded: () => void }) {
  const { t } = useLocale();
  const fx = useFormat();
  const { defaultTimezone } = useTenantSettings();
  const ids = useId();
  const [selection, setSelection] = useState<Selection>(new Map());
  const [fromDate, setFromDate] = useState(() => tenantToday(defaultTimezone));
  const [toDate, setToDate] = useState(() => tenantToday(defaultTimezone));
  const [inTime, setInTime] = useState('09:00');
  const [outTime, setOutTime] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [result, setResult] = useState<BulkEntryResultShape | null>(null);
  // The employee and department lists are fetched by the multi-select, which mounts only once this is opened.
  const [open, setOpen] = useState(false);

  const state = massEntryState({ employees: selection.size, fromDate, toDate, inTime, outTime, reason, busy, canWrite });
  const { plan } = state;
  const summaryParams = { employees: plan.employees, days: plan.days };

  const problemText = (): string | null => {
    switch (plan.problem) {
      case 'too-many-employees': return t('Mass entry is limited to {max} employees at a time.', { max: MASS_ENTRY_MAX_EMPLOYEES });
      case 'too-many-days': return t('Mass entry is limited to {max} days at a time.', { max: MASS_ENTRY_MAX_DAYS });
      case 'too-many-punches': return t(MASS_ENTRY_PUNCH_MATHS_KEY, { employees: plan.employees, days: plan.days, perDay: plan.punches / Math.max(1, plan.employees * plan.days), punches: fx.integer(plan.punches), max: fx.integer(MASS_ENTRY_MAX_PUNCHES) });
      case 'bad-range': return t('Choose a valid date range: the end date cannot be before the start date.');
      default: return null;
    }
  };
  const problem = problemText();

  const submit = async () => {
    if (!state.canSubmit) return;
    setBusy(true);
    setError('');
    setResult(null);
    try {
      const done = normaliseBulkEntryResult(await attendanceApi.events.bulkEntry({
        employeeIds: selectedIds(selection), fromDate, toDate, inTime,
        outTime: outTime || undefined, reason: reason.trim(),
      }));
      setResult(done);
      if (done.created > 0) onRecorded();
    } catch (err) {
      setError(`${t('Mass entry failed.')} ${describeBulkError(err, t)}`);
    } finally {
      setBusy(false);
    }
  };

  if (!canWrite) return null;

  if (!open) {
    return (
      <section className="surface p-4" aria-label={t('Mass entry')}>
        <SectionTitle icon={Users} title={t('Mass entry')} subtitle={t('Record the same attendance for many employees at once. A reason is required and is kept in the audit trail.')} />
        <button type="button" className="btn-secondary mt-4 w-full justify-center" onClick={() => setOpen(true)} aria-expanded={false}>{t('Open mass entry')}</button>
      </section>
    );
  }

  return (
    // Deliberately not a form element: Enter in the search box or on a checkbox must never record punches.
    <section className="surface p-4" aria-busy={busy} aria-label={t('Mass entry')}>
      <SectionTitle icon={Users} title={t('Mass entry')} subtitle={t('Record the same attendance for many employees at once. A reason is required and is kept in the audit trail.')} />
      <div className="mt-4 space-y-3">
        <EmployeeMultiSelect id={`${ids}-mass`} label={t('Employees to include')} selection={selection} onChange={setSelection} max={MASS_ENTRY_MAX_EMPLOYEES} disabled={busy} />
        <div className="grid grid-cols-2 gap-3">
          <input type="date" className="input w-full" value={fromDate} onChange={(e) => setFromDate(e.target.value)} aria-label={t('From date')} disabled={busy} />
          <input type="date" className="input w-full" value={toDate} onChange={(e) => setToDate(e.target.value)} aria-label={t('To date')} disabled={busy} />
          <div>
            <label htmlFor={`${ids}-in`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('In time')}</label>
            <input id={`${ids}-in`} type="time" className="input w-full" value={inTime} onChange={(e) => setInTime(e.target.value)} disabled={busy} />
          </div>
          <div>
            <label htmlFor={`${ids}-out`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('Out time (optional)')}</label>
            <input id={`${ids}-out`} type="time" className="input w-full" value={outTime} onChange={(e) => setOutTime(e.target.value)} disabled={busy} />
          </div>
        </div>
        <p className="text-xs text-slate-500 dark:text-slate-400">{t('Times are in {zone}.', { zone: defaultTimezone })}</p>
        {state.overnight && <p role="status" className="text-xs text-slate-500 dark:text-slate-400">{t('Overnight shift: the out time is recorded on the next day.')}</p>}
        <div>
          <label htmlFor={`${ids}-reason`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('Reason (required)')}</label>
          <textarea id={`${ids}-reason`} className="input min-h-20 w-full" maxLength={MASS_ENTRY_MAX_REASON} value={reason} onChange={(e) => setReason(e.target.value)}
            placeholder={t('For example: the biometric device was offline')} disabled={busy} />
        </div>

        {problem && selection.size > 0 && <p role="alert" className="text-xs text-rose-700 dark:text-rose-400">{problem}</p>}
        {plan.problem === null && (
          <p className="text-sm font-medium text-slate-800 dark:text-slate-100">{t(MASS_ENTRY_SUMMARY_KEY, summaryParams)}</p>
        )}
        <button type="button" onClick={submit} disabled={!state.canSubmit} className="btn-primary w-full justify-center">
          {plan.problem === null ? t(MASS_ENTRY_SUBMIT_KEY, summaryParams) : t('Mass entry')}
        </button>
        {plan.problem === 'no-employees' && <p className="text-xs text-slate-500">{t('Select at least one employee.')}</p>}

        {error && <p role="alert" className="text-sm text-rose-700 dark:text-rose-400">{error}</p>}
        {result && (
          <div className="space-y-2" role="status">
            <div className="flex flex-wrap gap-1.5">
              <StatusChip tone={outcomeTone(result.created, result.failed)} label={t('Requested {requested}: {created} recorded, {duplicates} already recorded, {failed} refused.', { requested: result.requested, created: result.created, duplicates: result.duplicates, failed: result.failed })} />
            </div>
            {result.errors.length > 0 && (
              <div className="max-h-56 overflow-auto rounded-lg border border-slate-200 dark:border-white/10">
                <table className="w-full text-xs">
                  <caption className="sr-only">{t('Entries that were refused')}</caption>
                  <thead className="sticky top-0 bg-slate-50 dark:bg-slate-900">
                    <tr>
                      <th scope="col" className="px-3 py-2 text-start font-semibold text-slate-500">{t('Employee')}</th>
                      <th scope="col" className="px-3 py-2 text-start font-semibold text-slate-500">{t('Date')}</th>
                      <th scope="col" className="px-3 py-2 text-start font-semibold text-slate-500">{t('Reason')}</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-slate-100 dark:divide-white/[0.06]">
                    {result.errors.map((e, i) => (
                      <tr key={`${e.employeeCode}-${e.date}-${i}`}>
                        <td className="px-3 py-2 font-mono text-slate-700 dark:text-slate-300">{e.employeeCode}</td>
                        <td className="px-3 py-2 text-slate-600 dark:text-slate-400">{fx.date(e.date)}</td>
                        <td className="px-3 py-2 text-slate-800 dark:text-slate-200">{e.message}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        )}
      </div>
    </section>
  );
}
