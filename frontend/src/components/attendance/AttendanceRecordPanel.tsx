'use client';

import { useCallback, useEffect, useId, useMemo, useReducer, useRef, useState, type ReactNode } from 'react';
import { CalendarClock, RefreshCw, RotateCcw, Square } from 'lucide-react';
import { attendanceApi } from '../../api/attendance';
import { useLocale } from '../../contexts/LocaleContext';
import { useTenantSettings } from '../../contexts/TenantSettingsContext';
import { useAttendanceProcessJob } from '../../hooks/useAttendanceProcessJob';
import { describeBulkError } from '../../lib/attendanceBulkError';
import {
  MASS_ENTRY_MAX_DAYS,
  MASS_ENTRY_MAX_EMPLOYEES,
  MASS_ENTRY_MAX_PUNCHES,
  MASS_ENTRY_MAX_REASON,
  MASS_ENTRY_PUNCH_MATHS_KEY,
  MASS_ENTRY_SUBMIT_KEY,
  MASS_ENTRY_SUMMARY_KEY,
  inclusiveDays,
  massEntryState,
  normaliseBulkEntryResult,
  tenantToday,
} from '../../lib/attendanceBulkEntry';
import {
  initialMassRun,
  isTransientBulkError,
  massRunReducer,
  planChunks,
  recordedIds,
  retryIds,
  runSummary,
  type PersonStatus,
} from '../../lib/attendanceMassRun';
import { selectedIds, type Selection } from '../../lib/attendanceSelection';
import { useFormat } from '../../hooks/useFormat';
import { StatusChip } from '../StatusChip';
import { EmployeeMultiSelect } from './EmployeeMultiSelect';
import { ProcessJobProgress } from './ProcessJobProgress';
import { SectionTitle } from './attendanceUi';

interface Props {
  /** The plain punch form (one employee, now), for users who may punch but not enter for a selection; null when they may not punch. */
  single: ReactNode | null;
  /** attendance.write AND attendance.bulk_import: may record for a selection of employees (one or many). */
  canMass: boolean;
  /** attendance.write: may start processing for the people just recorded. */
  canProcess: boolean;
  /** Called after punches were recorded or processed, so the page reloads what it shows. */
  onRecorded: () => void;
}

/**
 * The Live Dashboard's one place to record attendance. With entry rights there is no mode to choose: one list, tick one person
 * or many, set the days and times, give a reason. Without them, the plain punch form.
 */
export function AttendanceRecordPanel({ single, canMass, canProcess, onRecorded }: Props) {
  const { t } = useLocale();
  if (!single && !canMass) return null;

  return (
    <section className="surface p-4" aria-label={t('Record attendance')}>
      <SectionTitle icon={CalendarClock} title={t('Record attendance')}
        subtitle={canMass ? t('Tick one or more employees, set the days and times, and give a reason. Every entry is kept in the audit trail.') : t('Save a web, mobile or kiosk punch for one employee now.')} />
      <div className="mt-3">
        {canMass ? <MassEntry canProcess={canProcess} onRecorded={onRecorded} /> : single}
      </div>
    </section>
  );
}

const STATUS_TONE: Record<PersonStatus, 'emerald' | 'amber' | 'rose' | 'slate'> = { done: 'emerald', partial: 'amber', notDone: 'rose', waiting: 'slate' };
/** A long range refused for the same rule repeats one reason per day: show the first few, then a count. */
const MAX_REASONS_SHOWN = 2;
const sleep = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function MassEntry({ canProcess, onRecorded }: { canProcess: boolean; onRecorded: () => void }) {
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
  const [run, dispatch] = useReducer(massRunReducer, initialMassRun);
  const [filter, setFilter] = useState<PersonStatus | 'all'>('all');
  const stopRef = useRef(false);
  const mountedRef = useRef(true);
  /** The entry as submitted, so a retry resends exactly that even if the fields were edited since. */
  const sentRef = useRef<{ fromDate: string; toDate: string; inTime?: string; outTime?: string; reason: string } | null>(null);
  const processAskedRef = useRef<{ scope: 'all' | 'selected'; expected: number }>({ scope: 'selected', expected: 0 });
  const process = useAttendanceProcessJob(onRecorded);

  useEffect(() => () => { mountedRef.current = false; }, []);

  const running = run.phase === 'running';
  const state = massEntryState({ employees: selection.size, fromDate, toDate, inTime, outTime, reason, busy: running, canWrite: true });
  const { plan } = state;
  const summary = runSummary(run);

  const problem = (() => {
    switch (plan.problem) {
      case 'too-many-employees': return t('Mass entry is limited to {max} employees at a time.', { max: MASS_ENTRY_MAX_EMPLOYEES });
      case 'too-many-days': return t('Mass entry is limited to {max} days at a time.', { max: MASS_ENTRY_MAX_DAYS });
      case 'too-many-punches': return t(MASS_ENTRY_PUNCH_MATHS_KEY, { employees: plan.employees, days: plan.days, perDay: plan.punches / Math.max(1, plan.employees * plan.days), punches: fx.integer(plan.punches), max: fx.integer(MASS_ENTRY_MAX_PUNCHES) });
      case 'bad-range': return t('Choose a valid date range: the end date cannot be before the start date.');
      default: return null;
    }
  })();

  const send = useCallback(async (people: Array<{ id: number; code: string; name: string }>) => {
    const entry = sentRef.current;
    if (!entry || people.length === 0) return;
    const chunks = planChunks(people.map((p) => p.id));
    stopRef.current = false;
    dispatch({ type: 'start', people, days: inclusiveDays(entry.fromDate, entry.toDate) ?? 1, chunksTotal: chunks.length });
    let recorded = 0;
    for (const chunk of chunks) {
      if (stopRef.current || !mountedRef.current) break;
      for (let attempt = 0; ; attempt++) {
        try {
          const result = normaliseBulkEntryResult(await attendanceApi.events.bulkEntry({ employeeIds: chunk, ...entry }));
          recorded += result.created;
          if (mountedRef.current) dispatch({ type: 'chunkDone', ids: chunk, result });
          break;
        } catch (err) {
          // Each chunk is idempotent (a repeat comes back "already recorded"), so a lost answer or a busy company is retried.
          if (isTransientBulkError(err) && attempt < 2 && !stopRef.current) { await sleep(800 * (attempt + 1)); continue; }
          if (mountedRef.current) dispatch({ type: 'chunkFailed', ids: chunk, message: describeBulkError(err, t) });
          break;
        }
      }
    }
    if (!mountedRef.current) return;
    dispatch({ type: 'end' });
    if (recorded > 0) onRecorded();
  }, [onRecorded, t]);

  const submit = () => {
    if (!state.canSubmit) return;
    sentRef.current = { fromDate, toDate, inTime: inTime || undefined, outTime: outTime || undefined, reason: reason.trim() };
    setFilter('all');
    const order = selectedIds(selection);
    void send(order.map((id) => { const p = selection.get(id)!; return { id, code: p.employeeCode, name: p.fullName }; }));
  };

  const retry = () => {
    const again = new Set(retryIds(run));
    void send(run.people.filter((p) => again.has(p.id)).map(({ id, code, name }) => ({ id, code, name })));
  };

  const processNow = () => {
    const entry = sentRef.current;
    const people = recordedIds(run);
    if (!entry || people.length === 0) return;
    processAskedRef.current = { scope: 'selected', expected: people.length };
    void process.start({ fromDate: entry.fromDate, toDate: entry.toDate, employeeIds: people });
  };

  // The people stay selected (HR often records a second entry for the same team); the reason must be given again.
  const newEntry = () => { dispatch({ type: 'reset' }); setReason(''); };

  const shownPeople = useMemo(() => (filter === 'all' ? run.people : run.people.filter((p) => p.status === filter)), [run.people, filter]);
  const timesLabel = (inAt?: string, outAt?: string) => (inAt && outAt ? `${inAt}–${outAt}` : inAt ? t('In at {time}', { time: inAt }) : t('Out at {time}', { time: outAt ?? '' }));
  const statusLabel = (s: PersonStatus) => ({ done: t('Recorded'), partial: t('Partly recorded'), notDone: t('Not recorded'), waiting: t('Pending') }[s]);
  const toProcess = recordedIds(run).length;
  const toRetry = retryIds(run).length;
  const locked = running || run.phase !== 'idle';
  // While running the bar is neutral; once ended its colour says how it went: all recorded, some, or none.
  const barColour = running ? 'bg-sapphire'
    : summary.done === summary.total ? 'bg-emerald-500'
      : summary.done + summary.partial > 0 ? 'bg-amber-500' : 'bg-rose-500';

  return (
    // Deliberately not a form element: Enter in the search box or on a checkbox must never record punches.
    <div className="space-y-4" aria-busy={running}>
      {run.phase !== 'idle' && sentRef.current && (
        // While a run is shown the form folds into one line, so the progress view and the daily records below stay in reach.
        <p className="rounded-lg bg-slate-50 px-3 py-2 text-xs text-slate-600 dark:bg-white/[0.04] dark:text-slate-300">
          <span className="font-semibold text-slate-800 dark:text-slate-100">
            {t(MASS_ENTRY_SUMMARY_KEY, { employees: run.people.length, days: inclusiveDays(sentRef.current.fromDate, sentRef.current.toDate) ?? 1 })}
          </span>
          {' · '}{fx.date(sentRef.current.fromDate)}{sentRef.current.toDate !== sentRef.current.fromDate && <> – {fx.date(sentRef.current.toDate)}</>}
          {' · '}{timesLabel(sentRef.current.inTime, sentRef.current.outTime)}
          {' · '}<span className="italic">{sentRef.current.reason}</span>
        </p>
      )}
      {run.phase === 'idle' && <div className="grid gap-5 lg:grid-cols-[minmax(0,1fr)_minmax(0,1fr)]">
        <EmployeeMultiSelect id={`${ids}-mass`} label={t('Employees to include')} selection={selection} onChange={setSelection}
          max={MASS_ENTRY_MAX_EMPLOYEES} disabled={locked} listMaxHeightClass="max-h-52" />
        <fieldset className="min-w-0 space-y-3" disabled={locked}>
          <legend className="sr-only">{t('Attendance to record')}</legend>
          <div className="grid grid-cols-2 gap-x-3 gap-y-2">
            <div>
              <label htmlFor={`${ids}-from`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('From date')}</label>
              <input id={`${ids}-from`} type="date" className="input w-full" value={fromDate} onChange={(e) => setFromDate(e.target.value)} />
            </div>
            <div>
              <label htmlFor={`${ids}-to`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('To date')}</label>
              <input id={`${ids}-to`} type="date" className="input w-full" value={toDate} onChange={(e) => setToDate(e.target.value)} />
            </div>
            <div>
              <label htmlFor={`${ids}-in`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('In time')}</label>
              <input id={`${ids}-in`} type="time" className="input w-full" value={inTime} onChange={(e) => setInTime(e.target.value)} />
            </div>
            <div>
              <label htmlFor={`${ids}-out`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('Out time')}</label>
              <input id={`${ids}-out`} type="time" className="input w-full" value={outTime} onChange={(e) => setOutTime(e.target.value)} />
            </div>
          </div>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Enter an in time, an out time, or both.')} {t('Times are in {zone}.', { zone: defaultTimezone })}
            {state.overnight && <> {t('Overnight shift: the out time is recorded on the next day.')}</>}
          </p>
          <div>
            <label htmlFor={`${ids}-reason`} className="mb-1 block text-xs font-medium text-slate-600 dark:text-slate-300">{t('Reason (required)')}</label>
            <input id={`${ids}-reason`} className="input w-full" maxLength={MASS_ENTRY_MAX_REASON} value={reason} onChange={(e) => setReason(e.target.value)}
              placeholder={t('For example: the biometric device was offline')} onKeyDown={(e) => { if (e.key === 'Enter') e.preventDefault(); }} />
          </div>
          {problem && selection.size > 0 && <p role="alert" className="text-xs text-rose-700 dark:text-rose-400">{problem}</p>}
          {plan.problem === 'no-employees' && <p className="text-xs text-slate-500">{t('Select at least one employee.')}</p>}
          {run.phase === 'idle' && (
            <button type="button" onClick={submit} disabled={!state.canSubmit} className="btn-primary w-full justify-center">
              {plan.problem === null ? t(MASS_ENTRY_SUBMIT_KEY, { employees: plan.employees, days: plan.days }) : t('Record attendance')}
            </button>
          )}
        </fieldset>
      </div>}

      {run.phase !== 'idle' && (
        <div className="space-y-3 rounded-xl border border-slate-200 p-3 dark:border-white/10" aria-label={t('Mass entry progress')}>
          <div className="flex flex-wrap items-center justify-between gap-2">
            <p role="status" className="text-sm font-semibold text-slate-800 dark:text-slate-100">
              {running ? t('Recording attendance… {percent}%', { percent: summary.percent })
                : run.phase === 'stopped' ? t('Stopped at {percent}%. The rest were not sent.', { percent: summary.percent })
                  : t('Finished: {done} of {total} employees recorded.', { done: summary.done, total: summary.total })}
            </p>
            {running && (
              <button type="button" className="btn-secondary px-3 py-1 text-xs" disabled={run.stopRequested} onClick={() => { stopRef.current = true; dispatch({ type: 'requestStop' }); }}>
                <Square className="h-3 w-3" aria-hidden="true" />{run.stopRequested ? t('Stopping…') : t('Stop')}
              </button>
            )}
          </div>
          <div className="flex items-center gap-2">
            <div role="progressbar" aria-label={t('Mass entry progress')} aria-valuemin={0} aria-valuemax={100} aria-valuenow={summary.percent}
              className="h-2.5 flex-1 overflow-hidden rounded-full bg-slate-100 dark:bg-white/10">
              <div className={`h-full rounded-full transition-[width] duration-300 ${barColour}`} style={{ width: `${summary.percent}%` }} />
            </div>
            <span className="w-10 text-end text-xs font-semibold tabular-nums text-slate-700 dark:text-slate-200">{summary.percent}%</span>
          </div>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('{created} punches recorded, {duplicates} already on file, {refused} refused.', { created: summary.created, duplicates: summary.duplicates, refused: summary.refused })}
          </p>

          <div role="group" aria-label={t('Show employees')} className="flex flex-wrap gap-1.5">
            {([['all', t('All'), summary.total], ['done', t('Recorded'), summary.done], ['partial', t('Partly recorded'), summary.partial], ['notDone', t('Not recorded'), summary.notDone], ['waiting', t('Pending'), summary.waiting]] as const)
              .filter(([key, , n]) => key === 'all' || n > 0)
              .map(([key, label, n]) => (
                <button key={key} type="button" aria-pressed={filter === key} onClick={() => setFilter(key)}
                  className={`rounded-full border px-2.5 py-1 text-xs font-medium ${filter === key ? 'border-sapphire bg-sapphire/10 text-sapphire' : 'border-slate-200 text-slate-600 hover:bg-slate-50 dark:border-white/10 dark:text-slate-300 dark:hover:bg-white/5'}`}>
                  {label} · {n}
                </button>
              ))}
          </div>

          <ul aria-label={t('Employees in this entry')} className="max-h-56 divide-y divide-slate-100 overflow-y-auto rounded-lg border border-slate-200 dark:divide-white/[0.06] dark:border-white/10">
            {shownPeople.map((p) => (
              <li key={p.id} className="flex items-start gap-2 px-3 py-2 text-sm">
                <span className="mt-0.5 font-mono text-xs text-slate-500">{p.code}</span>
                <span className="min-w-0 flex-1">
                  <span className="block truncate text-slate-800 dark:text-slate-100">{p.name}</span>
                  {p.reasons.length > 0 && (
                    <span className="block text-xs text-slate-500 dark:text-slate-400">
                      {p.reasons.slice(0, MAX_REASONS_SHOWN).join(' · ')}
                      {p.reasons.length > MAX_REASONS_SHOWN && <> {t('+{count} more', { count: p.reasons.length - MAX_REASONS_SHOWN })}</>}
                    </span>
                  )}
                </span>
                <StatusChip tone={STATUS_TONE[p.status]} label={statusLabel(p.status)} dot />
              </li>
            ))}
          </ul>

          {!running && (
            <div className="flex flex-wrap gap-2">
              {canProcess && toProcess > 0 && (
                <button type="button" className="btn-primary" onClick={processNow} disabled={process.busy}>
                  <RefreshCw className="h-4 w-4" aria-hidden="true" />
                  {t('Process {count, plural, one {# employee} other {# employees}} now', { count: toProcess })}
                </button>
              )}
              {toRetry > 0 && (
                <button type="button" className="btn-secondary" onClick={retry} disabled={process.busy}>
                  <RotateCcw className="h-4 w-4" aria-hidden="true" />
                  {t('Retry the {count, plural, one {# employee} other {# employees}} not recorded', { count: toRetry })}
                </button>
              )}
              <button type="button" className="btn-secondary" onClick={newEntry} disabled={process.busy}>{t('New entry')}</button>
            </div>
          )}
          {toProcess > 0 && !running && process.poll.phase === 'idle' && (
            <p className="text-xs text-slate-500 dark:text-slate-400">{t('Recorded punches appear in the daily records after processing.')}</p>
          )}
          <ProcessJobProgress poll={process.poll} asked={processAskedRef.current} />
        </div>
      )}
    </div>
  );
}
