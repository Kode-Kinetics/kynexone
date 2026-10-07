import client from './client';

/**
 * Employee sign-in access (api/employee-access). The login belongs to the employee record from the
 * moment it has a work email; HR never chooses a password, it hands out a one-time 8-digit welcome
 * code and the employee sets their own password. See the shared contract, section 4.
 *
 * CODES ARE SECRETS. `issueCodes` is the only call that returns one (`code`, and only when nothing
 * was emailed). Callers keep the response in component memory and drop it when the slip view
 * closes: never localStorage/sessionStorage, never a URL query, never a log line.
 */

/** One state per employee, as the API returns it (lowercase). */
export type EmployeeAccessState =
  | 'waiting_for_work_email'
  | 'not_started'
  | 'code_given'
  | 'active'
  | 'stopped'
  | 'blocked';

export const EMPLOYEE_ACCESS_STATES: readonly EmployeeAccessState[] = [
  'waiting_for_work_email', 'not_started', 'code_given', 'active', 'stopped', 'blocked',
];

/** GET api/employee-access/{employeeId} */
export interface EmployeeAccessDto {
  employeeId: number;
  employeeName: string;
  employeeCode: string;
  workEmail: string | null;
  state: EmployeeAccessState;
  codeExpiresAtUtc: string | null;
  codeIssuedByName: string | null;
  lastCodeExpiredAtUtc: string | null;
  lastSignInAtUtc: string | null;
  stoppedReason: string | null;
  blockedCode: string | null;
  blockedReason: string | null;
  canIssue: boolean;
  /** The tenant can email codes: "Email sign-in code" leads, "Print sign-in slip" is second. */
  emailDelivery?: boolean;
}

/** One printed (or emailed) welcome code. `code` is absent when it went by email. */
export interface IssuedWelcomeCode {
  employeeId: number;
  employeeName: string;
  arabicName?: string | null;
  employeeCode: string;
  /** What the employee signs in with (the login's email). Printed as "Your username". */
  username: string;
  department?: string | null;
  site?: string | null;
  code?: string;
  expiresAtUtc: string;
  /** The company's workspace slug, carried in the QR hash (`w=`) so the welcome page needs no typing. */
  tenantSlug?: string | null;
  /**
   * How this one code reaches the employee. Results can be mixed: whoever typed an employee's work
   * email always gets "print" for that employee, even when the company can email.
   */
  delivery?: 'email' | 'print';
}

export interface SkippedWelcomeCode {
  employeeId: number;
  reasonCode: string;
  reason: string;
}

/** POST api/employee-access/codes */
export interface IssueWelcomeCodesResult {
  issued: IssuedWelcomeCode[];
  skipped: SkippedWelcomeCode[];
  /** True only when EVERY issued code was emailed. Read each item's `delivery` for mixed results. */
  emailed: boolean;
  /** Present when some codes must be printed by hand; shown through a translated sentence, never raw. */
  deliveryMessage?: string | null;
}

export interface WorkEmailRow {
  employeeCode: string;
  workEmail: string;
}

/** POST api/employee-access/work-emails */
export interface WorkEmailBackfillResult {
  matched: Array<{ employeeId: number; employeeCode: string; employeeName: string; oldEmail: string | null; newEmail: string }>;
  notFound: string[];
  wrongDomain: Array<{ employeeCode: string; workEmail: string; expectedDomain: string }>;
  /**
   * Rows that can't be saved. `reason` is a code: `username_differs` (an active login keeps its
   * username, given in `username`), an in-file duplicate, or an email another employee uses.
   */
  conflicts: Array<{ employeeCode: string; workEmail: string; reason: string; username?: string | null }>;
  saved: number;
}

/** The API's batch ceilings (section 4). */
export const MAX_CODES_PER_REQUEST = 500;
export const MAX_WORK_EMAIL_ROWS = 2000;

export const employeeAccessApi = {
  get: (employeeId: number) =>
    client.get<EmployeeAccessDto>(`/api/employee-access/${employeeId}`).then((r) => r.data),

  /**
   * Give access / Give new code for one employee or many (max 500), or Reset sign-in for exactly one
   * `active` employee (a request with an active employee must carry one id: 400 `reset_is_single`).
   */
  // Email is the server's default when it can send mail, so only a request for a PRINTABLE code says so.
  issueCodes: (employeeIds: number[], delivery?: 'email' | 'print') =>
    client.post<IssueWelcomeCodesResult>('/api/employee-access/codes', delivery === 'print' ? { employeeIds, delivery } : { employeeIds }).then((r) => r.data),

  /** The work-email backfill from IT. `dryRun` writes nothing and returns the same buckets. */
  saveWorkEmails: (rows: WorkEmailRow[], dryRun: boolean) =>
    client.post<WorkEmailBackfillResult>('/api/employee-access/work-emails', { rows, dryRun }).then((r) => r.data),
};
