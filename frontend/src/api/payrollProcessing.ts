import client from './client';

/**
 * Payroll processing as a background job.
 *
 * Contract (backend, agreed with the processing builder):
 *   POST /api/payroll/runs/{runId}/process     -> 202 { runId, jobId, status: "Queued" }
 *     Idempotent: when a job is already queued or running for the run, the same job comes back.
 *   GET  /api/payroll/runs/{runId}/processing  -> ProcessingStatus (below)
 *
 * Kept out of ./payroll.ts on purpose; that file is being edited by the backend-side change and the two
 * are reconciled at merge. Once reconciled, payrollApi.processRun should delegate to startRunProcessing.
 */
export type ProcessingJobState = 'Idle' | 'Queued' | 'Running' | 'Succeeded' | 'Failed';

export interface ProcessingStart {
  runId: string;
  jobId: string;
  status: 'Queued' | ProcessingJobState;
}

export interface ProcessingStatus {
  runId: string;
  /** null while the run has never been processed (status Idle). */
  jobId: string | null;
  status: ProcessingJobState;
  processedEmployees: number | null;
  totalEmployees: number | null;
  startedAtUtc: string | null;
  finishedAtUtc: string | null;
  errorCode: string | null;
  errorMessage: string | null;
}

export const isProcessingActive = (s: ProcessingJobState | undefined): boolean => s === 'Queued' || s === 'Running';

const PROCESSING_STATES: readonly ProcessingJobState[] = ['Idle', 'Queued', 'Running', 'Succeeded', 'Failed'];

/** Narrow an untrusted response to the contract; anything else is treated as "no status", never guessed. */
export function parseProcessingStatus(runId: string, raw: unknown): ProcessingStatus | null {
  if (!raw || typeof raw !== 'object') return null;
  const r = raw as Record<string, unknown>;
  if (typeof r.status !== 'string' || !PROCESSING_STATES.includes(r.status as ProcessingJobState)) return null;
  const num = (v: unknown) => (typeof v === 'number' && Number.isFinite(v) ? v : null);
  const str = (v: unknown) => (typeof v === 'string' && v ? v : null);
  return {
    runId: typeof r.runId === 'string' ? r.runId : runId,
    jobId: str(r.jobId),
    status: r.status as ProcessingJobState,
    processedEmployees: num(r.processedEmployees),
    totalEmployees: num(r.totalEmployees),
    startedAtUtc: str(r.startedAtUtc),
    finishedAtUtc: str(r.finishedAtUtc),
    errorCode: str(r.errorCode),
    errorMessage: str(r.errorMessage),
  };
}

/** Queue processing for a run (or join the job already in flight). Refusals (409/422) reject as usual. */
export const startRunProcessing = (runId: string): Promise<ProcessingStart> =>
  client.post<ProcessingStart>(`/api/payroll/runs/${runId}/process`).then((r) => r.data);

export const getRunProcessingStatus = (runId: string): Promise<ProcessingStatus | null> =>
  client.get<unknown>(`/api/payroll/runs/${runId}/processing`).then((r) => parseProcessingStatus(runId, r.data));
