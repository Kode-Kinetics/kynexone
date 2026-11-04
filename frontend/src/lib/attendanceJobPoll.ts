/**
 * Polling a background job (GET /api/jobs/{id}) without a spinner that can lie.
 *
 * The attendance "Process" button enqueues a durable job (POST /api/attendance/process/async -> 202
 * { jobId, status, deduplicated, statusUrl }) and the screen then polls the job. This module is the
 * pure part: parse one answer, and fold events (enqueue, answer, error) into a state the screen draws.
 * It never decides success from the absence of an error: only a `Succeeded` answer ends in success, and a
 * run of unreadable / failed polls ends in an honest "lost contact" failure rather than spinning forever.
 */

export type JobState = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';

export interface JobSnapshot {
  id: string;
  state: JobState;
  completed: number;
  /** null while the job has not yet counted its work. */
  total: number | null;
  /** 0..100, or null when it cannot be known (indeterminate). */
  percent: number | null;
  message: string | null;
  /** The server's vouched error text (never a stack), only when failed. */
  error: string | null;
  result: Record<string, unknown> | null;
}

const STATES: Record<string, JobState> = {
  queued: 'queued', running: 'running', succeeded: 'succeeded', failed: 'failed', cancelled: 'cancelled',
};

const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
const str = (v: unknown): string | null => (typeof v === 'string' && v.trim() ? v : null);

/** One BackgroundJobDto -> JobSnapshot, or null when the answer is not a job we understand. */
export function parseJobSnapshot(raw: unknown): JobSnapshot | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  const id = str(r.id);
  const state = typeof r.status === 'string' ? STATES[r.status.toLowerCase()] : undefined;
  if (!id || !state) return null;

  const total = num(r.progressTotal);
  const completed = Math.max(0, num(r.progressCompleted) ?? 0);
  let percent = num(r.percentComplete);
  if (percent === null && total !== null && total > 0) percent = (100 * Math.min(completed, total)) / total;
  if (percent === null && state === 'succeeded') percent = 100;
  if (percent !== null) percent = Math.min(100, Math.max(0, percent));

  const result = r.result && typeof r.result === 'object' && !Array.isArray(r.result) ? (r.result as Record<string, unknown>) : null;
  return { id, state, completed, total, percent, message: str(r.progressMessage), error: state === 'failed' ? str(r.lastError) : null, result };
}

export type JobPollPhase = 'idle' | 'enqueuing' | 'polling' | 'succeeded' | 'failed' | 'cancelled';

export interface JobPollState {
  phase: JobPollPhase;
  jobId: string | null;
  job: JobSnapshot | null;
  /** True when the server handed back a job that was already queued or running for the same request. */
  deduplicated: boolean;
  /** Consecutive polls that failed or could not be read. */
  failures: number;
  error: string | null;
}

export const initialJobPoll: JobPollState = { phase: 'idle', jobId: null, job: null, deduplicated: false, failures: 0, error: null };

/** Consecutive failed polls tolerated before the screen stops and says it lost contact with the job. */
export const MAX_POLL_FAILURES = 5;

export type JobPollAction =
  | { type: 'enqueue' }
  | { type: 'enqueued'; jobId: string; deduplicated: boolean }
  | { type: 'enqueueFailed'; error: string }
  | { type: 'snapshot'; job: JobSnapshot }
  /** `fatal`: retrying cannot help (job not visible to the caller, not allowed). */
  | { type: 'pollFailed'; error: string; fatal: boolean }
  | { type: 'reset' };

const TERMINAL: readonly JobPollPhase[] = ['succeeded', 'failed', 'cancelled'];

export function jobPollReducer(state: JobPollState, action: JobPollAction): JobPollState {
  switch (action.type) {
    case 'reset':
      return initialJobPoll;
    case 'enqueue':
      return { ...initialJobPoll, phase: 'enqueuing' };
    case 'enqueueFailed':
      return { ...initialJobPoll, phase: 'failed', error: action.error };
    case 'enqueued':
      return { ...initialJobPoll, phase: 'polling', jobId: action.jobId, deduplicated: action.deduplicated };
    case 'snapshot': {
      if (TERMINAL.includes(state.phase) || state.phase !== 'polling' || action.job.id !== state.jobId) return state;
      const base = { ...state, job: action.job, failures: 0 };
      switch (action.job.state) {
        case 'succeeded': return { ...base, phase: 'succeeded', error: null };
        case 'failed': return { ...base, phase: 'failed', error: action.job.error };
        case 'cancelled': return { ...base, phase: 'cancelled', error: null };
        default: return { ...base, phase: 'polling' };
      }
    }
    case 'pollFailed': {
      if (state.phase !== 'polling') return state;
      const failures = state.failures + 1;
      if (action.fatal || failures >= MAX_POLL_FAILURES) return { ...state, phase: 'failed', failures, error: action.error };
      return { ...state, failures };
    }
  }
}

export const shouldKeepPolling = (state: JobPollState): boolean => state.phase === 'polling';

/** 1 s between polls, backing off to at most 8 s while the API is struggling. */
export function nextPollDelayMs(state: JobPollState): number {
  return Math.min(8000, 1000 * 2 ** Math.min(state.failures, 3));
}
