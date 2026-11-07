'use client';

import { useEffect, useReducer, useRef } from 'react';
import { attendanceApi } from '../api/attendance';
import { jobsApi } from '../api/jobs';
import { useLocale } from '../contexts/LocaleContext';
import { describeBulkError } from '../lib/attendanceBulkError';
import { initialJobPoll, jobPollReducer, nextPollDelayMs, shouldKeepPolling } from '../lib/attendanceJobPoll';

export interface ProcessRequest {
  fromDate: string;
  toDate: string;
  /** Omitted: everyone in the caller's scope. */
  employeeIds?: number[];
}

/**
 * One attendance processing run as a background job: enqueue, then poll <c>GET /api/jobs/{id}</c> until it ends, with a
 * soft "taking longer than usual" flag. The job is the truth: success is only reported when the job says Succeeded, and
 * <paramref name="onSucceeded"/> fires once per job.
 */
export function useAttendanceProcessJob(onSucceeded: () => void) {
  const { t } = useLocale();
  const tRef = useRef(t);
  tRef.current = t;
  const [poll, dispatch] = useReducer(jobPollReducer, initialJobPoll);
  const succeededRef = useRef<string | null>(null);

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
    if (poll.phase === 'succeeded' && poll.jobId && succeededRef.current !== poll.jobId) {
      succeededRef.current = poll.jobId;
      onSucceeded();
    }
  }, [poll.phase, poll.jobId, onSucceeded]);

  const start = async (request: ProcessRequest) => {
    dispatch({ type: 'enqueue' });
    try {
      const queued = await attendanceApi.processAsync(request.employeeIds
        ? { fromDate: request.fromDate, toDate: request.toDate, employeeIds: request.employeeIds }
        : { fromDate: request.fromDate, toDate: request.toDate });
      dispatch({ type: 'enqueued', jobId: queued.jobId, deduplicated: queued.deduplicated, now: Date.now() });
    } catch (err) {
      dispatch({
        type: 'enqueueFailed',
        error: describeBulkError(err, tRef.current, tRef.current('You don\'t have permission to process attendance for this selection.'), tRef.current('Too many processing jobs are already running. Try again in a few minutes.')),
      });
    }
  };

  const busy = poll.phase === 'enqueuing' || poll.phase === 'polling';
  return { poll, busy, start };
}
