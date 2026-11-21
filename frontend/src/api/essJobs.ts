import client from './client';
import { jobsParams, normalizeJob, readJobsPage, type EssJob, type EssJobsPage } from '../lib/essJobs';

/**
 * Open positions for the signed-in employee (EssJobsController): the jobs open in their own company, read-only.
 * The company comes from the session, never from a parameter, and pay is not part of the reply.
 */
export const essJobsApi = {
  list: (params: { q?: string; page: number }): Promise<EssJobsPage> =>
    client.get<unknown>('/api/ess/jobs', { params: jobsParams(params) }).then((r) => readJobsPage(r.data)),
  /** 404 when the position is not (or no longer) visible to this employee. */
  get: (id: string): Promise<EssJob> =>
    client.get<unknown>(`/api/ess/jobs/${encodeURIComponent(id)}`).then((r) => normalizeJob(r.data)),
};
