import client from './client';
import { parseJobSnapshot, type JobSnapshot } from '../lib/attendanceJobPoll';

/**
 * Background jobs (JobsController). Jobs are enqueued by the owning module's endpoint (e.g.
 * POST /api/attendance/process/async); this reads one back. A job the caller may not see answers 404, never 403.
 */
export const jobsApi = {
  /** null when the answer is not a job we understand (the caller treats that as a failed poll, not as progress). */
  get: (id: string): Promise<JobSnapshot | null> =>
    client.get<unknown>(`/api/jobs/${encodeURIComponent(id)}`).then((r) => parseJobSnapshot(r.data)),
};
