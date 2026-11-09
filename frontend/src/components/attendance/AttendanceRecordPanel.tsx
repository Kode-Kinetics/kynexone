'use client';

import { useCallback, useEffect, useId, useMemo, useReducer, useRef, useState, type ReactNode } from 'react';
import { CalendarClock, RefreshCw, RotateCcw, Square, X } from 'lucide-react';
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
  massRunReducer,
  planChunks,
  recordedIds,
  retryIds,
  runChunks,
  runSummary,
  trackMounted,
  type PersonStatus,
} from '../../lib/attendanceMassRun';
import { selectedIds, type Selection } from '../../lib/attendanceSelection';
import { useFormat } from '../../hooks/useFormat';
import { StatusChip } from '../StatusChip';
import { ResultBanner, type ResultNext } from '../ui/ResultBanner';
import { Field } from '../ui/Field';
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
  /** Close the panel (the ghost Close in its header, and Esc). The page keeps it mounted, so nothing typed or running is lost. */
  onClose: () => void;
  /** Reports a mass-entry run in progress (people sent so far, of all), or null when none is running, so the page can show it while closed. */
  onRunProgress?: (progress: { done: number; total: number } | null) => void;
  /** Take the user to the day's records (the next action of the result banner when there is nothing to process). */
  onReview?: () => void;
}

/**
 * The Live Dashboard's one place to record attendance. With entry rights there is no mode to choose: one list, tick one person
 * or many, set the days and times, give a reason. Without them, the plain punch form.
 */
export function AttendanceRecordPanel({ single, canMass, canProcess, onRecorded, onClose, onRunProgress, onReview }: Props) {
  const { t } = useLocale();
  if (!single && !canMass) return null;

  return (
    // scroll-mb keeps the submit clear of the assistant button when the panel is scrolled into view.
    <section className="surface scroll-mb-16 p-4" aria-label={t('Record attendance')}
      onKeyDown={(e) => { if (e.key === 'Escape' && !e.defaultPrevented) { e.preventDefault(); onClose(); } }}>
      {/* Under 400 px Close sits above the title, so the title keeps the whole width instead of collapsing beside it. */}
      <div className="flex flex-col-reverse items-end gap-2 xs:flex-row xs:items-start xs:justify-between xs:gap-3">
        <div className="min-w-0 self-stretch xs:flex-1">
          <SectionTitle icon={CalendarClock} title={t('Record attendance')}
            subtitle={canMass ? t('Tick one or more employees, set the days and times, and give a reason. Every entry is kept in the audit trail.') : t('Save a web, mobile or kiosk punch for one employee now.')} />
        </div>
        <button type="button" className="btn-ghost shrink-0 max-sm:min-h-11" onClick={onClose}><X className="h-4 w-4" aria-hidden="true" />{t('Close')}</button>
      </div>
      <div className="mt-3">
        {canMass ? <MassEntry canProcess={canProcess} onRecorded={onRecorded} onRunProgress={onRunProgress} onReview={onReview} /> : single}
      </div>
    </section>
  );
}

const STATUS_TONE: Record<PersonStatus, 'emerald' | 'amber' | 'rose' | 'slate'> = { done: 'emerald', partial: 'amber', notDone: 'rose', waiting: 'slate' };
/** A long range refused for the same rule repeats one reason per day: show the first few, then a count. */
const MAX_REASONS_SHOWN = 2;
const sleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

function MassEntry({ canProcess, onRecorded, onRunProgress, onReview }: { canProcess: boolean; onRecorded: () => void; onRunProgress?: (progress: { done: number; total: number } | null) => void; onReview?: () => void }) {
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
  const [bannerDismissed, setBannerDismissed] = useState(false);
  const stopRef = useRef(false);
  const mountedRef = useRef(true);
  /** The entry as submitted, so a retry resends exactly that even if the fields were edited since. */
  const sentRef = useRef<{ fromDate: string; toDate: string; inTime?: string; outTime?: string; reason: string } | null>(null);
  const processAskedRef = useRef<{ scope: 'all' | 'selected'; expected: number }>({ scope: 'selected', expected: 0 });
  const process = useAttendanceProcessJob(onRecorded);

  // Set on setup, not just cleared on cleanup: Strict Mode runs setup, cleanup, setup again on the same ref.
  useEffect(() => trackMounted(mountedRef), []);

  const running = run.phase === 'running';
  const state = massEntryState({ employees: selection.size, fromDate, toDate, inTime, outTime, reason, busy: running, canWrite: true });
  const { plan } = state;
  const summary = runSummary(run);
  // Tell the page how far a run is, so the header can show "Recording 40 of 120" while this panel is closed.
  const sentSoFar = summary.total - summary.waiting;
  useEffect(() => {
    onRunProgress?.(running ? { done: sentSoFar, total: summary.total } : null);
  }, [onRunProgress, running, sentSoFar, summary.total]);
  useEffect(() => () => onRunProgress?.(null), [onRunProgress]);

  const problem = (() => {
    switch (plan.problem) {
      case 'too-many-employees': return t('Mass entry is limited to {max} employees at a time.', { max: MASS_ENTRY_MAX_EMPLOYEES });
      case 'too-many-days': return t('Mass entry is limited to {max} days at a time.', { max: MASS_ENTRY_MAX_DAYS });
      case 'too-many-punches': return t(MASS_ENTRY_PUNCH_MATHS_KEY, { employees: plan.employees, days: plan.days, perDay: plan.punches / Math.max(1, plan.employees * plan.days), punches: fx.integer(plan.punches), max: fx.integer(MASS_ENTRY_MAX_PUNCHES) });
      case 'bad-range': return t('Choose a valid date range: the end date cannot be before the start date.');
      default: return null;
    }
  })();

  /** `retry` resends a subset of the run on screen: everyone else keeps their status, so the results and "Process now" still cover them. */
  const send = useCallback(async (people: Array<{ id: number; code: string; name: string }>, retry = false) => {
    const entry = sentRef.current;
    if (!entry || people.length === 0) return;
    const chunks = planChunks(people.map((p) => p.id));
    stopRef.current = false;
    setBannerDismissed(false);
    if (retry) dispatch({ type: 'retry', ids: people.map((p) => p.id), chunksTotal: chunks.length });
    else dispatch({ type: 'start', people, days: inclusiveDays(entry.fromDate, entry.toDate) ?? 1, chunksTotal: chunks.length });
    const { recorded, finished } = await runChunks({
      chunks,
      sendChunk: async (ids) => normaliseBulkEntryResult(await attendanceApi.events.bulkEntry({ employeeIds: ids, ...entry })),
      dispatch,
      isStopped: () => stopRef.current,
      isMounted: () => mountedRef.current,
      describeError: (err) => describeBulkError(err, t),
      sleep,
    });
    if (finished && recorded > 0) onRecorded();
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
    void send(run.people.filter((p) => again.has(p.id)).map(({ id, code, name }) => ({ id, code, name })), true);
  };

  const processNow = () => {
    const entry = sentRef.current;
    const people = recordedIds(run);
    if (!entry || people.length === 0 || process.busy) return;
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
  const bannerNext: ResultNext | undefined = canProcess && toProcess > 0
    ? { label: t('Process now'), onClick: processNow }
    : onReview ? { label: t('Review {count, plural, one {# record} other {# records}}', { count: toProcess }), onClick: onReview } : undefined;
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
      {run.phase === 'idle' && <div className="grid gap-5 lg:grid-cols-2">
        <EmployeeMultiSelect id={`${ids}-mass`} label={t('Employees to include')} selection={selection} onChange={setSelection}
          max={MASS_ENTRY_MAX_EMPLOYEES} disabled={locked} listMaxHeightClass="max-h-52" />
        <fieldset className="min-w-0 space-y-3" disabled={locked}>
          <legend className="sr-only">{t('Attendance to record')}</legend>
          <div className="grid grid-cols-1 gap-x-3 gap-y-3 xs:grid-cols-2">
            <Field label={t('From date')} noInfo="a plain date with one obvious meaning">
              <input id={`${ids}-from`} type="date" className="input w-full min-w-0 max-sm:min-h-11" value={fromDate} onChange={(e) => setFromDate(e.target.value)} />
            </Field>
            <Field label={t('To date')} noInfo="a plain date with one obvious meaning">
              <input id={`${ids}-to`} type="date" className="input w-full min-w-0 max-sm:min-h-11" value={toDate} onChange={(e) => setToDate(e.target.value)} />
            </Field>
            <Field label={t('In time')} info={defaultTimezone ? t('The time of the in punch, in the company time zone. Leave it empty to record only an out time.') : t('The time of the in punch. Leave it empty to record only an out time.')}>
              <input id={`${ids}-in`} type="time" className="input w-full min-w-0 max-sm:min-h-11" value={inTime} onChange={(e) => setInTime(e.target.value)} />
            </Field>
            <Field label={t('Out time')} info={t('The time of the out punch. If it is the same as or earlier than the in time, it is recorded on the next day.')}>
              <input id={`${ids}-out`} type="time" className="input w-full min-w-0 max-sm:min-h-11" value={outTime} onChange={(e) => setOutTime(e.target.value)} />
            </Field>
          </div>
          <p className="text-xs text-slate-500 dark:text-slate-400">
            {t('Enter an in time, an out time, or both.')}{defaultTimezone && <> {t('Times are in {zone}.', { zone: defaultTimezone })}</>}
            {state.overnight && <> {t('Overnight shift: the out time is recorded on the next day.')}</>}
          </p>
          <Field label={t('Reason (required)')} noInfo="free text; the entry's reason is kept in the audit trail, stated in the panel subtitle">
            <input id={`${ids}-reason`} className="input w-full max-sm:min-h-11" maxLength={MASS_ENTRY_MAX_REASON} value={reason} onChange={(e) => setReason(e.target.value)}
              placeholder={t('For example: the biometric device was offline')} onKeyDown={(e) => { if (e.key === 'Enter') e.preventDefault(); }} />
          </Field>
          {problem && selection.size > 0 && <p role="alert" className="text-xs text-rose-700 dark:text-rose-400">{problem}</p>}
          {plan.problem === 'no-employees' && <p className="text-xs text-slate-500">{t('Select at least one employee.')}</p>}
          {run.phase === 'idle' && (
            // The zone keeps the submit clear of the assistant button when it is scrolled into view (scroll margin plus padding).
            <div data-submit-zone className="scroll-mb-16 pb-4">
              <button type="button" onClick={submit} disabled={!state.canSubmit} className="btn-primary w-full justify-center max-sm:min-h-11">
                {plan.problem === null ? t(MASS_ENTRY_SUBMIT_KEY, { employees: plan.employees, days: plan.days }) : t('Record attendance')}
              </button>
            </div>
          )}
        </fieldset>
      </div>}

      {run.phase !== 'idle' && (
        <div className="space-y-3 rounded-xl border border-line p-3" aria-label={t('Mass entry progress')}>
          {run.phase === 'finished' && summary.created > 0 && !bannerDismissed && sentRef.current && (
            // What was recorded, where the user is, and the one next step (process, or go and look at the records).
            <ResultBanner
              changed={t('{count, plural, one {# entry} other {# entries}} recorded for {date}', {
                count: summary.created,
                date: sentRef.current.toDate === sentRef.current.fromDate ? fx.date(sentRef.current.fromDate) : `${fx.date(sentRef.current.fromDate)} – ${fx.date(sentRef.current.toDate)}`,
              })}
              where={t('You are on the Live Dashboard. The records below update once the punches are processed.')}
              next={bannerNext}
              onDismiss={() => setBannerDismissed(true)}
            />
          )}
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
              <div className={`h-full rounded-full transition-[width] duration-base ${barColour}`} style={{ width: `${summary.percent}%` }} />
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
                <span className="min-w-0 flex-1">
                  <span className="block break-words text-fg"><bdi>{p.name}</bdi></span>
                  <span className="block text-meta text-fg-muted"><bdi className="font-mono">{p.code}</bdi></span>
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
