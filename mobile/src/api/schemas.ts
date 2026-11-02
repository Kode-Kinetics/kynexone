// ============================================================
// KynexOne Mobile — API response schemas (zod) and the route table
// ============================================================
//
// Import-free except for zod, so tests/apiSchemas.test.ts runs under node --test.
//
// Why: every response used to be a blind cast. When the backend shape moves (the web tier and the
// API deploy separately, and an OTA bundle can run against an older or newer API) the app showed
// NaN, `undefined` or a blank ErrorBoundary. Responses are now parsed at the client boundary.
//
// Rules the schemas follow:
//  - Objects are `.passthrough()` and almost every field is optional: the adapters already
//    tolerate missing fields, so a missing field is NOT a mismatch. What IS a mismatch is the
//    wrong container (HTML where an array belongs), or a field of the wrong kind where a number,
//    date or id is shown to the user (money that is "N/A", a list that is an object).
//  - Values are never copied into an error: payslip and profile data must not reach logs.

import { z } from 'zod';

const NUMERIC = /^-?\d+(\.\d+)?$/;
/** A number, or a string holding one: how money, counts and days arrive. null/absent allowed. */
const numLike = z.union([z.number().finite(), z.string().regex(NUMERIC), z.literal('')]).nullish();
const idLike = z.union([z.string(), z.number()]).nullish();
const text = z.string().nullish();
const flag = z.boolean().nullish();
const obj = <T extends z.ZodRawShape>(shape: T) => z.object(shape).passthrough();
const looseObj = obj({});
const list = <T extends z.ZodTypeAny>(item: T) => z.array(item);

/** A paged reply is either a bare array or { items | data: [...] , total? }. */
const paged = <T extends z.ZodTypeAny>(item: T) =>
  z.union([
    list(item),
    obj({ items: list(item).nullish(), data: list(item).nullish(), total: numLike, page: numLike, pageSize: numLike }),
  ]);

/** A write that may answer with the created row, 204, or an empty body. */
const writeResult = z.union([looseObj, z.null(), z.undefined(), z.literal('')]);

export const authMeSchema = obj({
  id: idLike,
  tenantId: idLike,
  employeeId: idLike,
  email: text,
  fullName: text,
  roles: list(z.union([z.string(), looseObj])).nullish(),
  permissions: z.unknown(),
  accessMode: text,
  requiresPasswordSetup: flag,
});

export const refreshSchema = obj({
  accessToken: z.string().min(1),
  refreshToken: z.string().min(1),
  expiresAtUtc: z.union([z.string().min(1), z.number()]),
});

const payslipRow = obj({
  id: idLike,
  month: numLike,
  year: numLike,
  netSalary: numLike,
  grossSalary: numLike,
  totalDeductions: numLike,
  loanDeductions: numLike,
  currency: text,
  status: text,
  paymentDate: text,
});

const payslipDetail = obj({
  id: idLike,
  netSalary: numLike,
  grossSalary: numLike,
  totalDeductions: numLike,
  currency: text,
  lines: list(obj({ name: text, nameAr: text, amount: numLike, type: text })).nullish(),
  reconciled: flag,
});

const leaveBalance = obj({
  leaveTypeId: idLike,
  leaveTypeName: text,
  allocated: numLike,
  entitled: numLike,
  used: numLike,
  pending: numLike,
  available: numLike,
});

const leaveType = obj({ id: idLike, nameEn: text, name: text, nameAr: text, isActive: flag, isHalfDayAllowed: flag });

const leaveRequest = obj({
  id: idLike,
  leaveTypeId: idLike,
  startDate: text,
  endDate: text,
  totalDays: numLike,
  status: text,
});

const approvalItem = obj({ id: idLike, status: text });

const notificationRow = obj({ id: idLike, title: text, message: text, body: text, isRead: flag, read: flag });

const hrRequest = obj({ id: idLike, status: text, subject: text, title: text });

const loanRow = obj({
  id: idLike,
  requestedAmount: numLike,
  approvedAmount: numLike,
  installmentAmount: numLike,
  totalRepaid: numLike,
  outstandingBalance: numLike,
  status: text,
});

const installmentRow = obj({ id: idLike, installmentNumber: numLike, amountDue: numLike, amountPaid: numLike, dueDate: text, status: text });

const letterRow = obj({ id: idLike, status: text });

export interface RouteSchema {
  name: string;
  method: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE';
  /** Matched against the request path with the query string removed and any base URL stripped. */
  pattern: RegExp;
  schema: z.ZodTypeAny;
}

const route = (method: RouteSchema['method'], pattern: RegExp, name: string, schema: z.ZodTypeAny): RouteSchema => ({
  method,
  pattern,
  name,
  schema,
});

const ID = '[^/]+';

/** Most specific first: the first match wins. */
export const ROUTE_SCHEMAS: RouteSchema[] = [
  // Auth
  route('GET', /^\/auth\/me$/, 'auth.me', authMeSchema),
  route('GET', /^\/auth\/mfa\/status$/, 'auth.mfaStatus', looseObj),
  route('POST', /^\/auth\/mfa\/enrollment\/start$/, 'auth.mfaEnrollmentStart', obj({ enrollmentToken: z.string().min(1), expiresInSeconds: numLike })),
  // ESS
  route('GET', /^\/ess\/dashboard$/, 'ess.dashboard', looseObj),
  route('GET', /^\/ess\/profile$/, 'ess.profile', looseObj),
  route('GET', new RegExp(`^/ess/payslips/${ID}/deductions$`), 'ess.payslipDeductions', looseObj),
  route('GET', new RegExp(`^/ess/payslips/${ID}$`), 'ess.payslipDetail', payslipDetail),
  route('GET', /^\/ess\/payslips$/, 'ess.payslips', list(payslipRow)),
  route('GET', /^\/ess\/deductions$/, 'ess.deductions', looseObj),
  route('GET', /^\/ess\/documents$/, 'ess.documents', list(looseObj)),
  route('GET', /^\/ess\/policies$/, 'ess.policies', list(looseObj)),
  route('GET', /^\/ess\/announcements$/, 'ess.announcements', z.union([list(looseObj), looseObj])),
  route('GET', /^\/ess\/team$/, 'ess.team', list(looseObj)),
  route('GET', /^\/ess\/hr-requests\/my$/, 'ess.hrRequests', list(hrRequest)),
  route('GET', new RegExp(`^/ess/hr-requests/${ID}$`), 'ess.hrRequest', looseObj),
  route('GET', /^\/ess\/leave\/balance$/, 'ess.leaveBalance', list(leaveBalance)),
  route('GET', /^\/ess\/notification-preferences$/, 'ess.notificationPreferences', looseObj),
  route('PUT', /^\/ess\/notification-preferences$/, 'ess.notificationPreferencesSave', looseObj),
  route('GET', /^\/ess\/attendance-verification$/, 'ess.attendanceVerification', z.unknown()),
  route('GET', /^\/ess\/document-requests\/types$/, 'ess.letterTypes', list(looseObj)),
  route('GET', /^\/ess\/document-requests$/, 'ess.letters', list(letterRow)),
  route('POST', /^\/ess\/document-requests$/, 'ess.letterCreate', looseObj),
  route('POST', /^\/ess\/hr-requests$/, 'ess.hrRequestCreate', writeResult),
  route('POST', /^\/ess\/documents$/, 'ess.documentUpload', writeResult),
  route('POST', /^\/ess\/profile\/photo$/, 'ess.profilePhoto', obj({ photoUrl: text })),
  route('POST', /^\/ess\/ai\/ask$/, 'ess.aiAsk', obj({ answer: text })),
  // Attendance
  route('GET', /^\/attendance\/(daily|events\/raw|regularization\/my)$/, 'attendance.list', paged(looseObj)),
  route('POST', /^\/attendance\/punch\/(mobile|kiosk)$/, 'attendance.punch', writeResult),
  // Leave
  route('GET', /^\/leave\/types$/, 'leave.types', list(leaveType)),
  route('GET', new RegExp(`^/leave/balances/employee/${ID}$`), 'leave.balancesForEmployee', list(leaveBalance)),
  route('GET', /^\/leave\/balances$/, 'leave.balances', paged(leaveBalance)),
  route('GET', /^\/leave\/requests$/, 'leave.requests', paged(leaveRequest)),
  route('GET', new RegExp(`^/leave/requests/${ID}$`), 'leave.request', looseObj),
  route('GET', /^\/leave\/holidays\/range$/, 'leave.holidays', z.union([list(looseObj), looseObj])),
  route('POST', /^\/leave\/requests$/, 'leave.requestCreate', looseObj),
  route('POST', new RegExp(`^/leave/requests/${ID}/(withdraw|cancel)$`), 'leave.requestChange', writeResult),
  // Overtime
  route('GET', /^\/overtime\/requests$/, 'overtime.requests', paged(looseObj)),
  route('GET', /^\/overtime\/policies$/, 'overtime.policies', list(looseObj)),
  route('GET', /^\/overtime\/calculations$/, 'overtime.calculations', list(looseObj)),
  route('POST', /^\/overtime\/requests(\/.*)?$/, 'overtime.write', writeResult),
  // Approvals
  route('GET', /^\/approval-requests$/, 'approvals.list', paged(approvalItem)),
  route('GET', new RegExp(`^/approval-requests/${ID}/change-detail$`), 'approvals.changeDetail', looseObj),
  route('GET', new RegExp(`^/approval-requests/${ID}$`), 'approvals.item', looseObj),
  route('POST', new RegExp(`^/approval-requests/${ID}/(decisions|send-back)$`), 'approvals.decide', writeResult),
  route('GET', new RegExp(`^/employees/${ID}/workflow-approvers$`), 'employees.approvers', z.union([looseObj, list(looseObj)])),
  route('GET', /^\/employees$/, 'employees.list', paged(looseObj)),
  // Notifications and mobile
  route('GET', new RegExp(`^/mobile/notifications/${ID}$`), 'mobile.notifications', list(notificationRow)),
  route('GET', new RegExp(`^/mobile/dashboard/${ID}$`), 'mobile.dashboard', looseObj),
  route('GET', /^\/notifications\/preferences$/, 'notifications.preferences', looseObj),
  route('PUT', /^\/notifications\/preferences$/, 'notifications.preferencesSave', looseObj),
  // Finance (loans)
  route('GET', /^\/finance\/loans$/, 'loans.list', obj({ total: numLike, items: list(loanRow) })),
  route('GET', new RegExp(`^/finance/loans/${ID}/installments$`), 'loans.installments', list(installmentRow)),
  route('GET', /^\/finance\/loans\/(types\/offered|types|policies)$/, 'loans.reference', list(looseObj)),
  route('GET', /^\/finance\/loans\/eligibility$/, 'loans.eligibility', looseObj),
  route('POST', /^\/finance\/loans$/, 'loans.create', looseObj),
  route('GET', /^\/features\/disabled-keys$/, 'features.disabledKeys', list(z.string())),
];

export class ResponseShapeError extends Error {
  readonly route: string;
  /** Dotted paths and zod issue codes only; never the received values. */
  readonly issues: string[];
  constructor(routeName: string, issues: string[], message = 'Unexpected response shape') {
    super(message);
    this.name = 'ResponseShapeError';
    this.route = routeName;
    this.issues = issues;
  }
}

/** Path without query string and without a leading http(s)://host or /api prefix. */
export function normalizeRequestPath(url: string): string {
  const noQuery = url.split(/[?#]/)[0] ?? '';
  const noHost = noQuery.replace(/^https?:\/\/[^/]+/i, '');
  const withSlash = noHost.startsWith('/') ? noHost : `/${noHost}`;
  return withSlash.replace(/^\/api(?=\/)/, '').replace(/\/+$/, '') || '/';
}

export function findRouteSchema(method: string, url: string): RouteSchema | undefined {
  const path = normalizeRequestPath(url);
  const m = method.toUpperCase();
  return ROUTE_SCHEMAS.find((r) => r.method === m && r.pattern.test(path));
}

/**
 * Validates an unwrapped response. Returns the data untouched (passthrough keeps unknown fields).
 * Unlisted routes are passed through: the table is the contract for what the app consumes.
 */
export function validateResponse<T>(method: string, url: string, data: T): T {
  const match = findRouteSchema(method, url);
  if (!match) return data;
  const result = match.schema.safeParse(data);
  if (result.success) return data;
  throw new ResponseShapeError(
    match.name,
    result.error.issues.slice(0, 5).map((i) => `${i.path.join('.') || '(root)'}:${i.code}`),
  );
}

export function validateRefreshResponse(data: unknown): z.infer<typeof refreshSchema> {
  const result = refreshSchema.safeParse(data);
  if (!result.success) {
    throw new ResponseShapeError('auth.refresh', result.error.issues.slice(0, 3).map((i) => `${i.path.join('.') || '(root)'}:${i.code}`));
  }
  return result.data;
}
