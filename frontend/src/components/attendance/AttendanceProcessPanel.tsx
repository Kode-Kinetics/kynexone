'use client';

import { useEffect, useReducer, useRef, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { attendanceApi } from '../../api/attendance';
import { jobsApi } from '../../api/jobs';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { describeBulkError } from '../../lib/attendanceBulkError';
import { inclusiveDays, tenantToday } from '../../lib/attendanceBulkEntry';
import { processedMismatch } from '../../lib/attendanceBulkResult';
import { initialJobPoll, jobPollReducer, nextPollDelayMs, shouldKeepPolling } from '../../lib/attendanceJobPoll';
import { selectedIds, type Selection } from '../../lib/attendanceSelection';
import { EmployeeMultiSelect } from './EmployeeMultiSelect';
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
  const tRef = useRef(t);
  tRef.current = t;
  const { defaultTimezone } = useTenantSettings();
  const [fromDate, setFromDate] = useState(() => tenantToday(defaultTimezone));
  const [toDate, setToDate] = useState(() => tenantToday(defaultTimezone));
  const [scope, setScope] = useState<'all' | 'selected'>('all');
  const [selection, setSelection] = useState<Selection>(new Map());
  const [poll, dispatch] = useReducer(jobPollReducer, initialJobPoll);
  const processedRef = useRef<string | null>(null);
  /** What the run was asked to cover, fixed at submit so it cannot drift while the job runs. */
  const askedRef = useRef<{ scope: 'all' | 'selected'; expected: number }>({ scope: 'all', expected: 0 });

  const rangeOk = inclusiveDays(fromDate, toDate) !== null;
  const busy = poll.phase === 'enqueuing' || poll.phase === 'polling';
  const needsPeople = scope === 'selected' && selection.size === 0;
  const canStart = !blocked && !busy && rangeOk && !needsPeople;

  // Poll the job until it ends. Each answer produces a new state, which schedules the next poll.
  useEffect(() => {
    if (!shouldKeepPolling(poll) || !poll.jobId) return;
    const jobId = poll.jobId;
    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        const job = await jobsApi.get(jobId);
        if (cancelled) return;
        if (job) dispatch({ type: 'snapshot', job, now: Date.now() });
        else dispatch({ type: 'pollFailed', error: tRef.current('We lost contact with the processing job. It may still be running; check the daily records in a moment.'), fatal: false });
      } catch (err) {
        if (cancelled) return;
        const status = (err as { response?: { status?: number } })?.response?.status;
        // 403: the job was accepted but this caller may not read job status. Not a failure of the job.
        if (status === 403) dispatch({ type: 'pollDenied' });
        else dispatch({ type: 'pollFailed', error: describeBulkError(err, tRef.current), fatal: status === 404 });
      }
    }, poll.job || poll.failures > 0 ? nextPollDelayMs(poll) : 600);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [poll]);

  // Soft timeout: after about ten minutes without a progress change, say so (the job is not given up on).
  useEffect(() => {
    if (poll.phase !== 'polling') return;
    const id = setInterval(() => dispatch({ type: 'checkStall', now: Date.now() }), 30_000);
    return () => clearInterval(id);
  }, [poll.phase]);

  useEffect(() => {
    if (poll.phase === 'succeeded' && poll.jobId && processedRef.current !== poll.jobId) {
      processedRef.current = poll.jobId;
      onProcessed();
    }
  }, [poll.phase, poll.jobId, onProcessed]);

  const submit = async () => {
    if (!canStart) return;
    askedRef.current = { scope, expected: selection.size };
    dispatch({ type: 'enqueue' });
    try {
      const body = scope === 'selected' ? { fromDate, toDate, employeeIds: selectedIds(selection) } : { fromDate, toDate };
      const queued = await attendanceApi.processAsync(body);
      dispatch({ type: 'enqueued', jobId: queued.jobId, deduplicated: queued.deduplicated, now: Date.now() });
    } catch (err) {
      dispatch({
        type: 'enqueueFailed',
        error: describeBulkError(err, t, t('You don\'t have permission to process attendance for this selection.'), t('Too many processing jobs are already running. Try again in a few minutes.')),
      });
    }
  };

  const employeesDone = typeof poll.job?.result?.employees === 'number' ? poll.job.result.employees : null;
  const mismatch = poll.phase === 'succeeded' ? processedMismatch({ ...askedRef.current, result: poll.job?.result ?? null }) : null;
  const percent = poll.job?.percent ?? null;

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

        {busy && (
          <div role="status" className="space-y-1.5">
            {poll.job?.state === 'queued' && <p className="text-xs text-slate-500">{t('Queued — waiting for a worker.')}</p>}
            {poll.stalled && <p className="text-xs text-amber-700 dark:text-amber-400">{t('Processing is taking longer than usual. The job may still be running; check the daily records later.')}</p>}
            {poll.deduplicated && <p className="text-xs text-slate-500">{t('This request was already running. Showing its progress.')}</p>}
            <div
              role="progressbar" aria-label={t('Processing progress')} aria-valuemin={0} aria-valuemax={100}
              aria-valuenow={percent === null ? undefined : Math.round(percent)}
              className="h-2 overflow-hidden rounded-full bg-slate-100 dark:bg-white/10"
            >
              <div className={`h-full rounded-full bg-sapphire ${percent === null ? 'w-1/3 animate-pulse' : ''}`} style={percent === null ? undefined : { width: `${percent}%` }} />
            </div>
            {poll.job?.total ? <p className="text-xs text-slate-500">{t('Step {done} of {total}', { done: poll.job.completed, total: poll.job.total })}</p> : null}
          </div>
        )}
        {mismatch && (
          <p role="alert" className="text-sm font-medium text-amber-700 dark:text-amber-400">{t('Processed {processed}, expected {expected}. Check the daily records.', mismatch)}</p>
        )}
        {poll.phase === 'untracked' && <p role="status" className="text-sm text-amber-700 dark:text-amber-400">{t('Processing started, but you do not have access to view its progress. Check the daily records later.')}</p>}
        {poll.phase === 'succeeded' && !mismatch && (
          <p role="status" className="text-sm text-emerald-700 dark:text-emerald-400">
            {employeesDone !== null
              ? t('Attendance processed for {employees, plural, one {# employee} other {# employees}}.', { employees: employeesDone })
              : t('Attendance processed into daily records and payroll impacts.')}
          </p>
        )}
        {poll.phase === 'failed' && (
          <div role="alert" className="space-y-0.5 text-sm text-rose-700 dark:text-rose-400">
            <p className="font-medium">{poll.jobId ? t('Processing did not finish.') : t('Could not start processing.')}</p>
            {poll.error && <p className="text-xs">{poll.error}</p>}
          </div>
        )}
        {poll.phase === 'cancelled' && <p role="status" className="text-sm text-amber-700 dark:text-amber-400">{t('Processing was cancelled.')}</p>}
      </div>
    </section>
  );
}
