'use client';

import { FormEvent, useEffect, useReducer, useRef, useState } from 'react';
import { RefreshCw } from 'lucide-react';
import { attendanceApi } from '../../api/attendance';
import { jobsApi } from '../../api/jobs';
import { useLocale } from '../../contexts/LocaleContext';
import { describeBulkError } from '../../lib/attendanceBulkError';
import { inclusiveDays } from '../../lib/attendanceBulkEntry';
import { initialJobPoll, jobPollReducer, nextPollDelayMs, shouldKeepPolling } from '../../lib/attendanceJobPoll';
import { selectedIds, type Selection } from '../../lib/attendanceSelection';
import { EmployeeMultiSelect } from './EmployeeMultiSelect';
import { SectionTitle } from './attendanceUi';

/** The most people the server accepts in an explicit processing selection. */
const MAX_SELECTED = 2000;
const today = () => new Date().toISOString().slice(0, 10);

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
  const [fromDate, setFromDate] = useState(today);
  const [toDate, setToDate] = useState(today);
  const [scope, setScope] = useState<'all' | 'selected'>('all');
  const [selection, setSelection] = useState<Selection>(new Map());
  const [poll, dispatch] = useReducer(jobPollReducer, initialJobPoll);
  const processedRef = useRef<string | null>(null);

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
        if (job) dispatch({ type: 'snapshot', job });
        else dispatch({ type: 'pollFailed', error: tRef.current('We lost contact with the processing job. It may still be running; check the daily records in a moment.'), fatal: false });
      } catch (err) {
        if (cancelled) return;
        const status = (err as { response?: { status?: number } })?.response?.status;
        dispatch({ type: 'pollFailed', error: describeBulkError(err, tRef.current), fatal: status === 403 || status === 404 });
      }
    }, poll.job || poll.failures > 0 ? nextPollDelayMs(poll) : 600);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [poll]);

  useEffect(() => {
    if (poll.phase === 'succeeded' && poll.jobId && processedRef.current !== poll.jobId) {
      processedRef.current = poll.jobId;
      onProcessed();
    }
  }, [poll.phase, poll.jobId, onProcessed]);

  const submit = async (event: FormEvent) => {
    event.preventDefault();
    if (!canStart) return;
    dispatch({ type: 'enqueue' });
    try {
      const body = scope === 'selected' ? { fromDate, toDate, employeeIds: selectedIds(selection) } : { fromDate, toDate };
      const queued = await attendanceApi.processAsync(body);
      dispatch({ type: 'enqueued', jobId: queued.jobId, deduplicated: queued.deduplicated });
    } catch (err) {
      dispatch({
        type: 'enqueueFailed',
        error: describeBulkError(err, t, scope === 'all'
          ? t('You can only process employees within your own access. Choose “Selected employees” and pick people you manage.')
          : undefined),
      });
    }
  };

  const employeesDone = typeof poll.job?.result?.employees === 'number' ? poll.job.result.employees : null;
  const percent = poll.job?.percent ?? null;

  return (
    <form onSubmit={submit} className="surface p-4" aria-busy={busy}>
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
        <button type="submit" disabled={!canStart} className="btn-primary w-full justify-center">{busy ? t('Processing attendance…') : t('Process Records')}</button>

        {busy && (
          <div role="status" className="space-y-1.5">
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
        {poll.phase === 'succeeded' && (
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
    </form>
  );
}
