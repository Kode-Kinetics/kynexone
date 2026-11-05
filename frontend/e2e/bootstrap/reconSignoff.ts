/**
 * Reconciliation sign-off for the stack-backed e2e specs and seeds.
 *
 * The API refuses to approve a period-owning payroll run until its manpower + value reconciliation has been signed off
 * for that processing of the run (409 reconciliation_not_reviewed). Anything that approves a run through the real API,
 * or clicks Approve in a browser against the real stack, calls `signOffReconciliation` first.
 *
 * Who signs matters. The signer must hold payroll.approve and must be neither the run's creator nor the person who
 * processed it (403 maker_checker_violation): pass the transport of an approver who did not create or process the run.
 * Use the approver the caller already has (the HR Manager "maker", the Payroll Manager pay approver); never the admin who
 * created and processed the run. Maker-checker on the approval itself is untouched.
 *
 * Disposable stacks only, like every bootstrap helper.
 */
import type { APIRequestContext } from '@playwright/test';
import type { ReconDto, SignoffRequest } from '../../src/api/payrollRecon';

export interface ReconResponse { status: number; body: any; text: string }
/** One HTTP call to the API as the signer. `path` includes the query string. */
export type ReconTransport = (method: 'GET' | 'POST', path: string, body?: unknown) => Promise<ReconResponse>;

/** The server's minimum for a written reason is 10 characters; this is comfortably above it. */
const REASON = 'E2E fixture: reviewed and accepted by the signer';
const PAGE = 200;

/** Wraps fetch for the seeds that already run outside Playwright's request context. */
export function fetchReconTransport(apiBase: string, token: string, companyId?: string): ReconTransport {
  return async (method, path, body) => {
    const headers: Record<string, string> = { 'Content-Type': 'application/json', Authorization: `Bearer ${token}` };
    if (companyId) headers['X-Company-Id'] = companyId;
    const response = await fetch(`${apiBase.replace(/\/$/, '')}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    const text = await response.text();
    let parsed: any = null;
    try { parsed = text ? JSON.parse(text) : null; } catch { /* kept in text */ }
    return { status: response.status, body: parsed, text };
  };
}

/** Wraps a Playwright APIRequestContext (its baseURL carries the API origin) for the specs. */
export function requestReconTransport(request: APIRequestContext, token: string): ReconTransport {
  return async (method, path, body) => {
    const headers = { Authorization: `Bearer ${token}` };
    const response = method === 'GET' ? await request.get(path, { headers }) : await request.post(path, { headers, data: body });
    const text = await response.text();
    let parsed: any = null;
    try { parsed = text ? JSON.parse(text) : null; } catch { /* kept in text */ }
    return { status: response.status(), body: parsed, text };
  };
}

/** False for an off-cycle run (never reconciled) and for one that already has an Active sign-off. */
export function reconNeedsSignoff(recon: Pick<ReconDto, 'mode' | 'signoff'>): boolean {
  return recon.mode !== 'NotApplicable' && recon.signoff?.status !== 'Active';
}

/**
 * The request the server accepts: bound to the version stamp, restating the flagged-employee count, with a reason for
 * every flagged item ("Kind:CODE") and for every NOT_PAID or UNEXPLAINED employee (the employee id as a string).
 */
export function buildReconSignoffRequest(recon: ReconDto, flaggedEmployeeCount: number, employeeIds: number[]): SignoffRequest {
  if (typeof recon.processVersion !== 'number' || !recon.snapshotHash) {
    throw new Error('[recon-signoff] the reconciliation carries no version stamp, so a sign-off cannot be bound to it.');
  }
  const full = recon.mode === 'Full';
  const items = full
    ? (recon.value.sections ?? []).flatMap(s => s.items.filter(i => i.flagged).map(i => `${s.kind}:${i.code}`))
    : [];
  return {
    expectedProcessVersion: recon.processVersion,
    expectedSnapshotHash: recon.snapshotHash,
    acknowledgedFlaggedEmployeeCount: full ? flaggedEmployeeCount : 0,
    explanations: [
      ...items.map(key => ({ scope: 'Item' as const, key, text: REASON })),
      ...(full ? employeeIds.map(id => ({ scope: 'Employee' as const, key: String(id), text: REASON })) : []),
    ],
    notes: 'Signed off by the e2e fixture bootstrap',
  };
}

const fail = (what: string, res: ReconResponse): never => {
  throw new Error(`[recon-signoff] ${what} failed: HTTP ${res.status}\n${res.text.slice(0, 600)}`);
};

async function employeeIds(call: ReconTransport, base: string, category: string): Promise<number[]> {
  const ids: number[] = [];
  for (let page = 1; ; page++) {
    const res = await call('GET', `${base}/employees?section=volume&category=${category}&page=${page}&pageSize=${PAGE}`);
    if (res.status !== 200) fail(`list the ${category} employees`, res);
    const rows: Array<{ employeeId: number }> = res.body?.items ?? [];
    ids.push(...rows.map(r => r.employeeId));
    if (rows.length === 0 || ids.length >= (res.body?.total ?? 0)) return ids;
  }
}

/**
 * Signs off the reconciliation of `runId` as the transport's user. Returns what happened; a refusal throws with the status
 * and the server's body, because a half-approved run is worse than a loud failure.
 */
export async function signOffReconciliation(call: ReconTransport, runId: string): Promise<'signed' | 'already_signed' | 'not_needed'> {
  const base = `/api/payroll/runs/${runId}/recon`;
  const read = await call('GET', base);
  if (read.status !== 200) fail('read the reconciliation', read);
  const recon = read.body as ReconDto;
  if (recon.mode === 'NotApplicable') return 'not_needed';
  if (recon.signoff?.status === 'Active') return 'already_signed';

  let flagged = 0;
  const ids: number[] = [];
  if (recon.mode === 'Full') {
    const counted = await call('GET', `${base}/employees?section=value&bucket=flagged&page=1&pageSize=1`);
    if (counted.status !== 200) fail('count the flagged employees', counted);
    flagged = Number(counted.body?.total ?? 0);
    ids.push(...await employeeIds(call, base, 'NOT_PAID'), ...await employeeIds(call, base, 'UNEXPLAINED'));
  }

  const res = await call('POST', `${base}/signoff`, buildReconSignoffRequest(recon, flagged, ids));
  if (res.status !== 200 && res.status !== 201) fail('sign off the reconciliation', res);
  return 'signed';
}
