'use client';

import { useRef, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { useAttendanceProcessJob } from '../../hooks/useAttendanceProcessJob';
import { inclusiveDays, tenantToday } from '../../lib/attendanceBulkEntry';
import { selectedIds, type Selection } from '../../lib/attendanceSelection';
import { EmployeeMultiSelect } from './EmployeeMultiSelect';
import { ProcessJobProgress } from './ProcessJobProgress';
import { SectionTitle } from './attendanceUi';

/** The most people the server accepts in an explicit processing selection. */
const MAX_SELECTED = 2000;

interface Props {
  /** Daily records or the employee list could not be loaded; processing is not offered on a blind screen. */
  blocked: boolean;
  /** Called once when a job finishes successfully, so the page can reload what it shows. */
  onProcessed: () => void;
}

/**
 * Process Attendance for everyone in the caller's scope or for chosen employees, as a background job with
 * visible progress. The job is the truth: success is only shown when the job reports Succeeded.
 */
export function AttendanceProcessPanel({ blocked, onProcessed }: Props) {
  const { t } = useLocale();
  const { defaultTimezone } = useTenantSettings();
  const [fromDate, setFromDate] = useState(() => tenantToday(defaultTimezone));
  const [toDate, setToDate] = useState(() => tenantToday(defaultTimezone));
  const [scope, setScope] = useState<'all' | 'selected'>('all');
  const [selection, setSelection] = useState<Selection>(new Map());
  const { poll, busy, start } = useAttendanceProcessJob(onProcessed);
  /** What the run was asked to cover, fixed at submit so it cannot drift while the job runs. */
  const askedRef = useRef<{ scope: 'all' | 'selected'; expected: number }>({ scope: 'all', expected: 0 });

  const rangeOk = inclusiveDays(fromDate, toDate) !== null;
  const needsPeople = scope === 'selected' && selection.size === 0;
  const canStart = !blocked && !busy && rangeOk && !needsPeople;

  const submit = async () => {
    if (!canStart) return;
    askedRef.current = { scope, expected: selection.size };
    await start(scope === 'selected' ? { fromDate, toDate, employeeIds: selectedIds(selection) } : { fromDate, toDate });
  };

  return (
    // Deliberately not a form element: Enter in the search box or on a checkbox must never start a job.
    <section className="surface p-4" aria-busy={busy} aria-label={t('Process Attendance')}>
      <SectionTitle icon={RefreshCw} title={t('Process Attendance')} subtitle={t('Transforms raw punches into daily attendance, exceptions, and payroll impacts.')} />
      <div className="mt-4 space-y-3">
        <input type="date" className="input w-full" value={fromDate} onChange={(e) => setFromDate(e.target.value)} aria-label={t('From date')} disabled={busy} />
        <input type="date" className="input w-full" value={toDate} onChange={(e) => setToDate(e.target.value)} aria-label={t('To date')} disabled={busy} />
        <fieldset className="space-y-1" disabled={busy}>
          <legend className="text-xs font-semibold text-slate-700 dark:text-slate-200">{t('Who to process')}</legend>
          <label className="flex items-center gap-2 text-sm"><input type="radio" name="process-scope" checked={scope === 'all'} onChange={() => setScope('all')} />{t('All employees')}</label>
          <label className="flex items-center gap-2 text-sm"><input type="radio" name="process-scope" checked={scope === 'selected'} onChange={() => setScope('selected')} />{t('Selected employees')}</label>
        </fieldset>
        {scope === 'selected' && (
          <EmployeeMultiSelect id="process" label={t('Employees to include')} selection={selection} onChange={setSelection} max={MAX_SELECTED} disabled={busy} />
        )}
        {!rangeOk && <p role="alert" className="text-xs text-rose-700 dark:text-rose-400">{t('Choose a valid date range: the end date cannot be before the start date.')}</p>}
        {needsPeople && <p className="text-xs text-slate-500">{t('Choose at least one employee.')}</p>}
        <button type="button" onClick={submit} disabled={!canStart} className="btn-primary w-full justify-center">{busy ? t('Processing attendance…') : t('Process Records')}</button>
        <ProcessJobProgress poll={poll} asked={askedRef.current} />
      </div>
    </section>
  );
}
