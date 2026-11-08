'use client';

import { useLocale } from '../../contexts/LocaleContext';
import { processedMismatch } from '../../lib/attendanceBulkResult';
import type { JobPollState } from '../../lib/attendanceJobPoll';

interface Props {
  poll: JobPollState;
  /** What the run was asked to cover, fixed at submit, to catch a job that processed fewer people than asked. */
  asked: { scope: 'all' | 'selected'; expected: number };
}

/** The live state of one attendance processing job: progress bar with %, then success, mismatch, failure or cancellation. */
export function ProcessJobProgress({ poll, asked }: Props) {
  const { t } = useLocale();
  const busy = poll.phase === 'enqueuing' || poll.phase === 'polling';
  const employeesDone = typeof poll.job?.result?.employees === 'number' ? poll.job.result.employees : null;
  const mismatch = poll.phase === 'succeeded' ? processedMismatch({ ...asked, result: poll.job?.result ?? null }) : null;
  const percent = poll.job?.percent ?? null;

  return (
    <>
      {busy && (
        <div role="status" className="space-y-1.5">
          {poll.job?.state === 'queued' && <p className="text-xs text-slate-500">{t('Queued — waiting for a worker.')}</p>}
          {poll.stalled && <p className="text-xs text-amber-700 dark:text-amber-400">{t('Processing is taking longer than usual. The job may still be running; check the daily records later.')}</p>}
          {poll.deduplicated && <p className="text-xs text-slate-500">{t('This request was already running. Showing its progress.')}</p>}
          <div className="flex items-center gap-2">
            <div
              role="progressbar" aria-label={t('Processing progress')} aria-valuemin={0} aria-valuemax={100}
              aria-valuenow={percent === null ? undefined : Math.round(percent)}
              className="h-2 flex-1 overflow-hidden rounded-full bg-slate-100 dark:bg-white/10"
            >
              <div className={`h-full rounded-full ${percent === null ? 'kx-skeleton w-1/3' : 'bg-sapphire'}`} style={percent === null ? undefined : { width: `${percent}%` }} />
            </div>
            {percent !== null && <span className="w-10 text-end text-xs font-semibold tabular-nums text-slate-700 dark:text-slate-200">{Math.round(percent)}%</span>}
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
    </>
  );
}
