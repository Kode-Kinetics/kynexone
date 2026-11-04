/**
 * Shared plumbing for the attendance bulk-operations specs (attendance-bulk-api.spec.ts,
 * attendance-bulk-ui.spec.ts): API client, employee pools, "find me a free window" probing, raw-log reads,
 * job polling, and the one place that provisions an employee-linked HR login.
 *
 * ── Re-runnable on one database ───────────────────────────────────────────────────────────────────────
 * Punches are idempotent (employee + instant + direction), so a second run that wrote the same minute on
 * the same day would see "0 created, 8 duplicates" and fail for a reason that is not a product bug.
 * `findFreeWindow` asks the product itself (the import DRY RUN, which writes nothing) for a run of past
 * days on which every punch the scenario will write is new and not payroll-locked, and the scenario uses
 * exactly that window. The minute/second of each time also comes from the clock, so two runs rarely probe
 * the same slot at all.
 *
 * ── Which employees ───────────────────────────────────────────────────────────────────────────────────
 * The fixture world gives IntelliFlow 14 employees (`INTELLIFLOW-KSA-E1..E14`). E1 may be on leave, E2/E3
 * are the portal logins (EMP1/EMP2). Everyone else that is Active is shared out between the API spec and
 * the UI spec by `planPools`, so the two never write the same employee-day (their counts stay exact).
 */
import { request as pwRequest, type APIRequestContext, type APIResponse } from '@playwright/test';
import { resolveTarget } from './identity/env';
import {
  apiLogin, INTELLIFLOW_ADMIN, INTELLIFLOW_HR_MGR, INTELLIFLOW_SLUG,
} from './helpers';

export const sleep = (ms: number): Promise<void> => new Promise((resolve) => setTimeout(resolve, ms));

/** The columns of the template, in order. */
export const TEMPLATE_HEADER = 'employeeCode,punchTimestamp,punchDirection,location,method';

/** The message an unknown code, an inactive employee AND an out-of-scope employee all get (invariant I2). */
export const UNKNOWN_CODE_MESSAGE_START = 'No active employee with this code is in your scope';

export async function newApi(): Promise<APIRequestContext> {
  return pwRequest.newContext({ baseURL: resolveTarget().apiBaseUrl });
}

export const bearer = (token: string): Record<string, string> => ({ Authorization: `Bearer ${token}` });

/**
 * One request. A 409 ("another bulk attendance operation is running in this company") and a 429 (too many
 * processing jobs) are retried, because both are transient by design; every other status is returned as is.
 */
export async function send(
  api: APIRequestContext, method: 'GET' | 'POST', url: string, token: string, data?: unknown, attempts = 4,
): Promise<APIResponse> {
  for (let attempt = 1; ; attempt++) {
    const response = method === 'GET'
      ? await api.get(url, { headers: bearer(token) })
      : await api.post(url, { headers: bearer(token), data: data ?? {} });
    if ((response.status() === 409 || response.status() === 429) && attempt < attempts) {
      await sleep(2_000 * attempt);
      continue;
    }
    return response;
  }
}

// ── Dates and times ─────────────────────────────────────────────────────────────────────────────────

const pad = (n: number): string => String(n).padStart(2, '0');

/** The UTC calendar day `back` days before today, as yyyy-MM-dd. */
export const dayBack = (back: number): string => new Date(Date.now() - back * 86_400_000).toISOString().slice(0, 10);

export function addDays(iso: string, days: number): string {
  const [y, m, d] = iso.split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d + days)).toISOString().slice(0, 10);
}

/** Minute and second of this run: they make the punch times differ from run to run. */
export function runClock(): { mm: string; ss: string } {
  const now = Date.now();
  return { mm: pad(Math.floor(now / 60_000) % 60), ss: pad(Math.floor(now / 1_000) % 60) };
}

/** One CSV data line in the template's five columns. */
export const csvRow = (code: string, day: string, time: string, direction: string): string =>
  `${code},${day} ${time},${direction},E2E bulk,Manual`;

export const csvFile = (...rows: string[]): string => [TEMPLATE_HEADER, ...rows].join('\n');

// ── Employees ───────────────────────────────────────────────────────────────────────────────────────

export interface Emp { id: number; code: string }

const numericSuffix = (code: string): number => Number(/(\d+)$/.exec(code)?.[1] ?? 0);

interface EmployeePage { items?: Array<{ id?: number; employeeCode?: string }>; total?: number }

export async function activeEmployees(api: APIRequestContext, token: string): Promise<Emp[]> {
  const found: Emp[] = [];
  for (let page = 1; page <= 20; page++) {
    const response = await send(api, 'GET', `/api/employees?status=Active&page=${page}&pageSize=100`, token);
    if (response.status() !== 200) throw new Error(`GET /api/employees -> ${response.status()}: ${(await response.text()).slice(0, 200)}`);
    const body = await response.json() as EmployeePage;
    const items = body.items ?? [];
    for (const e of items) if (typeof e.id === 'number' && e.employeeCode) found.push({ id: e.id, code: e.employeeCode });
    if (items.length === 0 || found.length >= (body.total ?? 0)) break;
  }
  return found.sort((a, b) => numericSuffix(a.code) - numericSuffix(b.code));
}

export interface Pools {
  /** API spec: files imported for these two. */
  apiImport: [Emp, Emp];
  /** API spec: mass entry and processing for these two. */
  apiBulk: [Emp, Emp];
  /** UI spec: one employee for the pasted file. */
  uiImport: Emp;
  /** UI spec: two employees for mass entry. */
  uiMass: [Emp, Emp];
  /** The employee a freshly invited HR login is linked to (their "own" record). */
  linkCandidate: Emp;
}

/** E1 (maybe on leave), E2 and E3 (portal logins) are left alone; the highest-numbered employee becomes the HR login's own record. */
export function planPools(all: Emp[]): Pools {
  const usable = all.filter((e) => ![1, 2, 3].includes(numericSuffix(e.code)));
  if (usable.length < 8) {
    throw new Error(
      `The attendance bulk specs need at least 8 Active employees besides E1-E3 and found ${usable.length} `
      + `(${all.map((e) => e.code).join(', ')}). The fixture world should provision 14 Active employees for IntelliFlow.`,
    );
  }
  const linkCandidate = usable[usable.length - 1];
  const pool = usable.slice(0, -1);
  return {
    apiImport: [pool[0], pool[1]],
    apiBulk: [pool[2], pool[3]],
    uiImport: pool[4],
    uiMass: [pool[5], pool[6]],
    linkCandidate,
  };
}

// ── Finding a free window of past days ──────────────────────────────────────────────────────────────

interface PreviewBody { totalRows: number; validRows: number }

/**
 * `days` consecutive past days (ascending, yyyy-MM-dd) on which every row `rowsFor(day)` returns is valid in the
 * import DRY RUN: the punches are new (not already recorded) and the day is not payroll-locked. Scans from 8 days
 * ago backwards (clear of the bootstrap's own five days of punches and well inside the 400-day limit), then, only
 * if the whole month behind is locked, the last few days.
 */
export async function findFreeWindow(
  api: APIRequestContext, token: string, days: number, rowsFor: (day: string) => string[],
): Promise<string[]> {
  const offsets = [...Array.from({ length: 38 }, (_, i) => 8 + i), 7, 6, 5, 4, 3];
  for (const back of offsets) {
    // `back` is how long ago the NEWEST day of the window is.
    const candidate = Array.from({ length: days }, (_, i) => addDays(dayBack(back), -(days - 1) + i));
    const rows = candidate.flatMap(rowsFor);
    const response = await send(api, 'POST', '/api/attendance/events/import-preview', token, { fileName: 'probe.csv', csvContent: csvFile(...rows) });
    if (response.status() !== 200) {
      throw new Error(`The import dry run used to find a free day answered ${response.status()}: ${(await response.text()).slice(0, 300)}`);
    }
    const body = await response.json() as PreviewBody;
    if (body.validRows === rows.length) return candidate;
  }
  throw new Error(
    'No run of free, unlocked past days was found in the last 45 days for this scenario. Every candidate day was either '
    + 'payroll-locked or already holds these exact punches. Re-run on a database that has not run this spec 40 times today, '
    + 'or check that the payroll lock does not cover the whole recent month.',
  );
}

// ── Reading what was written ────────────────────────────────────────────────────────────────────────

export interface RawPunch {
  employeeId: number | null;
  employeeCode: string;
  source: string;
  punchDirection: string;
  punchTimestampUtc: string;
  verificationMethod: string;
}

/** Every raw punch of one employee on the UTC day `day` (the scenarios punch mid-day, so local and UTC days agree), read as an unrestricted caller. */
export async function rawPunches(api: APIRequestContext, token: string, employeeId: number, day: string): Promise<RawPunch[]> {
  const url = `/api/attendance/events/raw?from=${day}&to=${day}&employeeId=${employeeId}&pageSize=100`;
  const response = await send(api, 'GET', url, token);
  if (response.status() !== 200) throw new Error(`GET ${url} -> ${response.status()}: ${(await response.text()).slice(0, 200)}`);
  return ((await response.json()) as { items?: RawPunch[] }).items ?? [];
}

export async function rawCount(api: APIRequestContext, token: string, employeeIds: number[], days: string[]): Promise<number> {
  let total = 0;
  for (const id of employeeIds) for (const day of days) total += (await rawPunches(api, token, id, day)).length;
  return total;
}

// ── Background jobs ─────────────────────────────────────────────────────────────────────────────────

export interface JobBody {
  id: string;
  status: string;
  lastError?: string | null;
  result?: { employees?: number; employeeDays?: number } | null;
}

/** Waits for the job to end and returns its final body. The caller asserts the status. */
export async function waitForJob(api: APIRequestContext, token: string, jobId: string, timeoutMs = 150_000): Promise<JobBody> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    const response = await send(api, 'GET', `/api/jobs/${jobId}`, token);
    if (response.status() !== 200) throw new Error(`GET /api/jobs/${jobId} -> ${response.status()}: ${(await response.text()).slice(0, 200)}`);
    const job = await response.json() as JobBody;
    if (['Succeeded', 'Failed', 'Cancelled'].includes(job.status)) return job;
    if (Date.now() > deadline) throw new Error(`Job ${jobId} was still ${job.status} after ${timeoutMs} ms.`);
    await sleep(1_000);
  }
}

// ── An HR login that IS an employee ─────────────────────────────────────────────────────────────────
//
// Invariant I1 (no self-attendance) can only be exercised by a caller whose token carries an employee_id, and the
// fixture world's HR Manager is a login with no person behind it. Rather than change the shared world, the spec
// provisions its own: the same invitation flow e2e/bootstrap/provision.ts uses for the portal logins, with the HR Manager
// role. Idempotent: once the login exists it is simply signed in.

export const LINKED_HR_EMAIL = 'hr.attendance.e2e@intelliflow.com';

export interface LinkedHr { token: string; employeeId: number; email: string }

const claim = (token: string, name: string): string | undefined => {
  try {
    const payload = JSON.parse(Buffer.from(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8')) as Record<string, unknown>;
    const value = payload[name];
    return value === undefined ? undefined : String(value);
  } catch {
    return undefined;
  }
};

async function tryLogin(api: APIRequestContext): Promise<string | null> {
  const response = await api.post('/api/auth/login', {
    data: { email: LINKED_HR_EMAIL, password: INTELLIFLOW_HR_MGR.password, tenantSlug: INTELLIFLOW_SLUG },
  });
  if (!response.ok()) return null;
  const body = await response.json() as { accessToken?: string; token?: string };
  return body.accessToken ?? body.token ?? null;
}

export async function linkedHrSession(api: APIRequestContext, candidate: Emp): Promise<LinkedHr> {
  let token = await tryLogin(api);
  if (!token) {
    const admin = await apiLogin(api, INTELLIFLOW_ADMIN.email, INTELLIFLOW_ADMIN.password, INTELLIFLOW_SLUG);
    const invite = await send(api, 'POST', '/api/access/employee-logins/invite', admin, {
      employeeId: candidate.id, email: LINKED_HR_EMAIL, accessMode: 'FullPortal', roles: [INTELLIFLOW_HR_MGR.role], confirmedWorkEmail: true,
    });
    if (![200, 201].includes(invite.status())) {
      throw new Error(
        `Could not invite an employee-linked HR login for ${candidate.code}: ${invite.status()} ${(await invite.text()).slice(0, 300)}\n`
        + 'The "own record" tests need a login that is an HR Manager AND an employee. Add one to the fixture world '
        + '(e2e/world.ts, as an employeePortalLogins entry with the HR Manager role) or free an unlinked Active employee.',
      );
    }
    const url = String(((await invite.json()) as { invitationUrl?: string }).invitationUrl ?? '');
    const invitationToken = decodeURIComponent(url.split('#token=')[1] ?? '');
    if (!invitationToken) throw new Error('The invitation came back without a link, so it cannot be accepted here (is mail transport configured?).');
    const accepted = await api.post('/api/auth/accept-invitation', {
      data: { invitationToken, newPassword: INTELLIFLOW_HR_MGR.password, tenantSlug: INTELLIFLOW_SLUG },
    });
    if (![200, 204].includes(accepted.status())) throw new Error(`Accepting the invitation answered ${accepted.status()}: ${(await accepted.text()).slice(0, 300)}`);
    token = await tryLogin(api);
  }
  if (!token) throw new Error(`${LINKED_HR_EMAIL} could not sign in after being invited (MFA enrolment required for this role?).`);
  const employeeId = Number(claim(token, 'employee_id'));
  if (!Number.isInteger(employeeId) || employeeId <= 0) throw new Error(`${LINKED_HR_EMAIL} signed in but its token has no employee_id claim, so it is not linked to a person.`);
  return { token, employeeId, email: LINKED_HR_EMAIL };
}
