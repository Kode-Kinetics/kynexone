import { expect, test } from '@playwright/test';
import {
  initialJobPoll,
  jobPollReducer,
  nextPollDelayMs,
  parseJobSnapshot,
  shouldKeepPolling,
  MAX_POLL_FAILURES,
  type JobPollState,
} from '../src/lib/attendanceJobPoll';

// GET /api/jobs/{id} -> BackgroundJobDto (JobsController): status is Queued|Running|Succeeded|Failed|Cancelled.
const dto = (over: Record<string, unknown> = {}) => ({
  id: 'job-1', jobType: 'attendance.process', status: 'Running', progressTotal: 10, progressCompleted: 4,
  percentComplete: 40, progressMessage: 'Processing', lastError: null, result: null, ...over,
});

test('parses a running job into progress the screen can draw', () => {
  const job = parseJobSnapshot(dto());
  expect(job).toMatchObject({ id: 'job-1', state: 'running', completed: 4, total: 10, percent: 40, error: null });
});

test('an unknown or missing status is not trusted as progress', () => {
  expect(parseJobSnapshot(dto({ status: 'Weird' }))).toBeNull();
  expect(parseJobSnapshot({})).toBeNull();
  expect(parseJobSnapshot(null)).toBeNull();
  expect(parseJobSnapshot('<html>')).toBeNull();
});

test('a job with no known total has no percentage (indeterminate), never NaN or 0%', () => {
  const job = parseJobSnapshot(dto({ progressTotal: null, percentComplete: null, progressCompleted: 0 }));
  expect(job?.total).toBeNull();
  expect(job?.percent).toBeNull();
});

test('percent is clamped to 0..100 and derived from counts when the server omits it', () => {
  expect(parseJobSnapshot(dto({ percentComplete: 140 }))?.percent).toBe(100);
  expect(parseJobSnapshot(dto({ percentComplete: undefined, progressTotal: 8, progressCompleted: 2 }))?.percent).toBe(25);
});

test('carries the result object of a finished job and the error of a failed one', () => {
  const done = parseJobSnapshot(dto({ status: 'Succeeded', result: { employees: 3, employeeDays: 9 } }));
  expect(done?.state).toBe('succeeded');
  expect(done?.result).toEqual({ employees: 3, employeeDays: 9 });
  const bad = parseJobSnapshot(dto({ status: 'Failed', lastError: 'The job did not complete.' }));
  expect(bad).toMatchObject({ state: 'failed', error: 'The job did not complete.' });
});

test('happy path: enqueue, poll, finish', () => {
  let s = jobPollReducer(initialJobPoll, { type: 'enqueue' });
  expect(s.phase).toBe('enqueuing');
  s = jobPollReducer(s, { type: 'enqueued', jobId: 'job-1', deduplicated: false });
  expect(s).toMatchObject({ phase: 'polling', jobId: 'job-1', deduplicated: false });
  expect(shouldKeepPolling(s)).toBe(true);
  s = jobPollReducer(s, { type: 'snapshot', job: parseJobSnapshot(dto())! });
  expect(s.phase).toBe('polling');
  expect(s.job?.percent).toBe(40);
  s = jobPollReducer(s, { type: 'snapshot', job: parseJobSnapshot(dto({ status: 'Succeeded', progressCompleted: 10, percentComplete: 100 }))! });
  expect(s.phase).toBe('succeeded');
  expect(shouldKeepPolling(s)).toBe(false);
});

test('a failed or cancelled job ends polling with that phase', () => {
  const polling: JobPollState = { ...initialJobPoll, phase: 'polling', jobId: 'job-1' };
  expect(jobPollReducer(polling, { type: 'snapshot', job: parseJobSnapshot(dto({ status: 'Failed', lastError: 'x' }))! }).phase).toBe('failed');
  expect(jobPollReducer(polling, { type: 'snapshot', job: parseJobSnapshot(dto({ status: 'Cancelled' }))! }).phase).toBe('cancelled');
});

test('a terminal state is final: a late snapshot cannot resurrect it', () => {
  const done = jobPollReducer({ ...initialJobPoll, phase: 'polling', jobId: 'job-1' },
    { type: 'snapshot', job: parseJobSnapshot(dto({ status: 'Succeeded' }))! });
  const after = jobPollReducer(done, { type: 'snapshot', job: parseJobSnapshot(dto({ status: 'Running' }))! });
  expect(after.phase).toBe('succeeded');
});

test('a snapshot for a different job is ignored', () => {
  const polling: JobPollState = { ...initialJobPoll, phase: 'polling', jobId: 'job-1' };
  const after = jobPollReducer(polling, { type: 'snapshot', job: parseJobSnapshot(dto({ id: 'other' }))! });
  expect(after).toBe(polling);
});

test('a deduplicated enqueue is remembered so the screen can say the job was already running', () => {
  const s = jobPollReducer(jobPollReducer(initialJobPoll, { type: 'enqueue' }), { type: 'enqueued', jobId: 'j', deduplicated: true });
  expect(s.deduplicated).toBe(true);
});

test('enqueue failure surfaces the server reason and stops', () => {
  const s = jobPollReducer(jobPollReducer(initialJobPoll, { type: 'enqueue' }), { type: 'enqueueFailed', error: 'You may not process every employee.' });
  expect(s).toMatchObject({ phase: 'failed', error: 'You may not process every employee.' });
  expect(shouldKeepPolling(s)).toBe(false);
});

test('transient poll errors are tolerated, a good answer resets the count', () => {
  let s: JobPollState = { ...initialJobPoll, phase: 'polling', jobId: 'job-1' };
  for (let i = 0; i < MAX_POLL_FAILURES - 1; i++) s = jobPollReducer(s, { type: 'pollFailed', error: 'network', fatal: false });
  expect(s.phase).toBe('polling');
  s = jobPollReducer(s, { type: 'snapshot', job: parseJobSnapshot(dto())! });
  expect(s.failures).toBe(0);
});

test('too many consecutive poll errors give up honestly instead of spinning forever', () => {
  let s: JobPollState = { ...initialJobPoll, phase: 'polling', jobId: 'job-1' };
  for (let i = 0; i < MAX_POLL_FAILURES; i++) s = jobPollReducer(s, { type: 'pollFailed', error: 'network', fatal: false });
  expect(s).toMatchObject({ phase: 'failed', error: 'network' });
});

test('a fatal poll error (job not visible / forbidden) stops at once', () => {
  const s = jobPollReducer({ ...initialJobPoll, phase: 'polling', jobId: 'job-1' }, { type: 'pollFailed', error: 'not found', fatal: true });
  expect(s.phase).toBe('failed');
});

test('poll delay backs off after failures and is capped', () => {
  const base: JobPollState = { ...initialJobPoll, phase: 'polling', jobId: 'j' };
  expect(nextPollDelayMs(base)).toBe(1000);
  expect(nextPollDelayMs({ ...base, failures: 2 })).toBeGreaterThan(1000);
  expect(nextPollDelayMs({ ...base, failures: 50 })).toBeLessThanOrEqual(8000);
});

test('reset returns to idle', () => {
  expect(jobPollReducer({ ...initialJobPoll, phase: 'failed', error: 'x' }, { type: 'reset' })).toEqual(initialJobPoll);
});
