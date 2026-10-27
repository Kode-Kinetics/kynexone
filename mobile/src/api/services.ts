// ============================================================
// KynexOne Mobile — API Services
// ============================================================
//
// Every request and response shape here was checked against the backend on the
// `develop` branch (backend-dotnet/Zayra.Api) and exercised against a live API.
// Where the backend has no endpoint for a feature, the function throws
// FeatureUnavailableError and the screen hides/disables the control
// (see src/config/features.ts).

import { Platform } from 'react-native';
import { apiDelete, apiGet, apiPost, apiPut, createPublicAuthClient, getApiClient, unwrapApiData } from './client';
import { tokenStorage, userStorage } from '@/storage';
import { getApiBaseUrl } from '@/config/server';
import { FEATURES, FeatureUnavailableError } from '@/config/features';
import { normalizeAccessMode } from '@/auth/accessPolicy';
import {
  normalizeEmail,
  publicInvitationInput,
  publicLoginInput,
  publicResetInput,
} from '@/auth/publicAuthInput';
import { DEFAULT_MIN_PASSWORD_LENGTH, normalizeWelcomeCode } from '@/auth/welcomeCode';
import { mapEmployeeProfile, type EmployeeProfileRecord } from './profileMapper';
import { mapDashboardExtras, type EmployeeHomeExtras } from './homeMappers';
import { holidaysApi } from './holidays';
import { fetchAllPages } from './paging';
import {
  classifyLoginResponse,
  classifyMfaStatus,
  normalizeTtl,
  parseRecoveryCodes,
  secretFromProvisioningUri,
  type MfaPrompt,
} from '@/auth/mfaFlow';
import { riyadhBusinessDate } from '@/utils/businessDate';
import { zonedWallTimeToUtcIso } from '@/features/overtime/overtimeLogic';
import {
  buildLocationFields,
  buildPunchBody,
  cacheIsFresh,
  clientPlatformHeaders,
  isSelfieBusy,
  parseWithdrawal,
  parseAttendanceVerification,
  busyRetryDelayMs,
  type AttendanceVerification,
  type PunchDirection,
  type VerificationCacheEntry,
  type WithdrawalResult,
} from '@/features/attendance/selfieAttendance';
import { COMPANY_TIME_ZONE, companyWallTimeToUtcIso } from '@/features/attendance/companyTime';
import {
  balanceFacts,
  mapLeaveFacts,
  decidableOvertime,
  mapOvertimeApproval,
  mapWorkflowApproval,
  mergeInbox,
  myDecisionsFrom,
  overtimeOutcome,
  workflowOutcome,
  type BalanceFacts,
  type DecisionOutcome,
  type InboxItem,
  type LeaveFacts,
  type MyDecision,
} from '@/features/approvals/approvalModel';
import { overlapping, teamTodayCounts, type OutEntry, type TeamDay, type TeamMemberToday } from '@/features/approvals/teamModel';
import { teamCalendarApi } from './teamCalendar';
import type {
  AuthUser,
  AuthTokens,
  AttendanceStatus,
  TodayAttendance,
  AttendanceDay,
  MobilePunchPayload,
  AttendanceRegularization,
  LeaveBalance,
  LeaveRequest,
  OvertimeRequest,
  Payslip,
  PayslipDetail,
  PayslipLine,
  EmployeeDocument,
  HRRequest,
  HRRequestComment,
  HRRequestType,
  ApprovalItem,
  AppNotification,
  AIAskPayload,
  AIAskResponse,
  EmployeeDashboard,
  ManagerDashboard,
  PaginatedResponse,
  Holiday,
} from '@/types';

type BackendPaged<T> = { items?: T[]; data?: T[]; total?: number; page?: number; pageSize?: number };

const fallbackTodayAttendance: TodayAttendance = {
  status: 'ABSENT',
  currentlyActive: false,
};

function itemsOf<T>(result: BackendPaged<T> | T[] | null | undefined): T[] {
  if (!result) return [];
  if (Array.isArray(result)) return result;
  return result.items ?? result.data ?? [];
}

/** YYYY-MM-DD for a Date, in the device's local calendar. */
function localIsoDate(d: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/**
 * Combine a local calendar date (YYYY-MM-DD) and wall-clock time (HH:mm) into a
 * UTC ISO timestamp. The backend binds these fields as DateTime; sending a bare
 * "HH:mm" made it silently use *today's* date.
 */
export function localDateTimeToUtcIso(date: string, time: string): string {
  const [y, m, d] = date.split('-').map(Number);
  const [hh, mm] = time.split(':').map(Number);
  const local = new Date(y, (m ?? 1) - 1, d ?? 1, hh ?? 0, mm ?? 0, 0, 0);
  if (Number.isNaN(local.getTime())) throw new Error(`Invalid date/time: ${date} ${time}`);
  return local.toISOString();
}

/** HH:mm on the company clock (Asia/Riyadh) for a UTC ISO timestamp, whatever zone the phone is in. */
function utcIsoToLocalHHmm(value?: string | null): string {
  if (!value) return '';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '';
  return new Intl.DateTimeFormat('en-GB', {
    timeZone: COMPANY_TIME_ZONE,
    hour: '2-digit',
    minute: '2-digit',
    hourCycle: 'h23',
  }).format(d);
}

function mapRole(roles?: string[]): AuthUser['role'] {
  const normalized = (roles ?? []).map((role) => role.toLowerCase());
  if (normalized.some((role) => role.includes('admin'))) return 'SUPER_ADMIN';
  if (normalized.some((role) => role.includes('finance'))) return 'FINANCE_APPROVER';
  if (normalized.some((role) => role.includes('payroll'))) return 'PAYROLL';
  if (normalized.some((role) => role.includes('hr'))) return 'HR';
  if (normalized.some((role) => role.includes('manager'))) return 'MANAGER';
  if (normalized.some((role) => role.includes('supervisor'))) return 'SUPERVISOR';
  return 'EMPLOYEE';
}

function mapPermissions(permissions?: string[]): AuthUser['permissions'] {
  const grouped = new Map<string, Set<string>>();
  (permissions ?? []).forEach((permission) => {
    const [module = permission, action = '*'] = permission.split('.');
    if (!grouped.has(module)) grouped.set(module, new Set());
    grouped.get(module)!.add(action);
  });
  return Array.from(grouped.entries()).map(([module, actions]) => ({
    module,
    actions: Array.from(actions),
  }));
}

function toAuthUser(raw: any): AuthUser {
  const roles = raw.roles ?? [];
  return {
    id: String(raw.id ?? ''),
    tenantId: String(raw.tenantId ?? ''),
    pendingResetNotice: raw.pendingResetNotice?.date ? { date: String(raw.pendingResetNotice.date) } : null,
    employeeId: raw.employeeId == null ? '' : String(raw.employeeId),
    username: raw.email ?? '',
    email: raw.email ?? '',
    fullName: raw.fullName ?? raw.name ?? raw.email ?? 'KynexOne User',
    name: raw.fullName ?? raw.name ?? raw.email ?? 'KynexOne User',
    role: mapRole(roles),
    accessMode: normalizeAccessMode(raw.accessMode),
    permissions: mapPermissions(raw.permissions),
    isFirstLogin: !!raw.requiresPasswordSetup,
    isActive: true,
    mustChangePassword: !!raw.requiresPasswordSetup,
  };
}

function toTokenExpiry(value: string | number | undefined): number {
  if (typeof value === 'number') return value;
  if (!value) return Date.now() + 60 * 60 * 1000;
  return new Date(value).getTime();
}

async function getCurrentEmployeeId(): Promise<number | null> {
  const user = await userStorage.getUser();
  const employeeId = Number(user?.employeeId);
  return Number.isFinite(employeeId) && employeeId > 0 ? employeeId : null;
}

async function requireEmployeeId(): Promise<number> {
  const employeeId = await getCurrentEmployeeId();
  if (!employeeId) throw new Error('Your login is not linked to an employee record. Please contact HR.');
  return employeeId;
}

/** Backend attendance statuses: Present, Late, Absent, Half day, On leave, Missing punch, Public holiday, Rest day, Weekend. */
export function mapStatus(status?: string): AttendanceStatus {
  const value = (status ?? '').toUpperCase().replace(/[\s-]+/g, '_');
  switch (value) {
    case 'PRESENT':
      return 'PRESENT';
    case 'LATE':
      return 'LATE';
    case 'LEAVE':
    case 'ON_LEAVE':
    case 'ONLEAVE':
      return 'ON_LEAVE';
    case 'HALF_DAY':
    case 'HALFDAY':
      return 'HALF_DAY';
    case 'HOLIDAY':
    case 'PUBLIC_HOLIDAY':
      return 'HOLIDAY';
    case 'WEEKEND':
    case 'REST_DAY':
    case 'WEEK_OFF':
      return 'WEEKEND';
    case 'MISSING_PUNCH':
    case 'MISSINGPUNCH':
      return 'MISSING_PUNCH';
    default:
      return 'ABSENT';
  }
}

function mapLeaveStatus(status?: string): LeaveRequest['status'] {
  const value = (status ?? '').toUpperCase();
  if (value.includes('APPROVED')) return 'APPROVED';
  if (value.includes('REJECT')) return 'REJECTED';
  if (value.includes('CANCEL') || value.includes('WITHDRAW')) return 'CANCELLED';
  if (value.includes('DRAFT')) return 'DRAFT';
  return 'PENDING';
}

function mapLeaveBalance(raw: any): LeaveBalance {
  return {
    leaveTypeId: String(raw.leaveTypeId ?? raw.id ?? ''),
    leaveTypeName: raw.leaveTypeName ?? raw.nameEn ?? raw.name ?? 'Leave',
    leaveTypeNameAr: raw.nameAr,
    allocated: Number(raw.allocated ?? raw.entitled ?? 0),
    used: Number(raw.used ?? 0),
    pending: Number(raw.pending ?? 0),
    available: Number(raw.available ?? 0),
    unit: 'DAYS',
  };
}

function mapLeaveRequest(raw: any): LeaveRequest {
  return {
    id: String(raw.id ?? ''),
    leaveTypeId: String(raw.leaveTypeId ?? ''),
    leaveTypeName: raw.leaveTypeName ?? 'Leave',
    startDate: raw.startDate ?? '',
    endDate: raw.endDate ?? '',
    totalDays: Number(raw.totalDays ?? 0),
    reason: raw.reason ?? '',
    status: mapLeaveStatus(raw.status),
    submittedAt: raw.submittedAtUtc ?? raw.createdAtUtc ?? '',
    approvalTimeline: [],
    canCancel: !['APPROVED', 'REJECTED', 'CANCELLED'].includes(mapLeaveStatus(raw.status)),
    canModify: mapLeaveStatus(raw.status) === 'DRAFT',
  };
}

/**
 * GET /ess/payslips (W2-D S6): finalised, non-voided slips, newest period first,
 * each with year, month, periodLabel and currency. `currency` is only a fallback
 * for an older backend that did not send it.
 */
function mapPayslip(raw: any, currency?: string): Payslip {
  const month = Number(raw.month ?? 0);
  const year = Number(raw.year ?? 0);
  const netSalary = Number(raw.netSalary ?? raw.totalNetSalary ?? raw.netPay ?? 0);
  const grossSalary = Number(raw.grossSalary ?? raw.totalGrossSalary ?? raw.grossPay ?? netSalary);
  const hasPeriod = month >= 1 && month <= 12 && year > 2000;
  const status = raw.status === 'Paid' ? 'PAID' : 'PROCESSED';
  return {
    id: String(raw.id ?? ''),
    month,
    year,
    // The screen formats the month in the UI language from year/month; this is only the server's label.
    periodLabel: raw.periodLabel || '',
    periodStart: hasPeriod ? localIsoDate(new Date(year, month - 1, 1)) : raw.paidFromDate ?? undefined,
    periodEnd: raw.paidToDate ?? undefined,
    grossSalary,
    grossPay: grossSalary,
    netSalary,
    netPay: netSalary,
    totalDeductions: Number(raw.totalDeductions ?? raw.deductions ?? 0),
    currency: raw.currency || currency || '',
    paymentDate: raw.paymentDate ?? undefined,
    paymentStatus: status,
    status,
    paidDays: raw.paidDays ?? undefined,
    publishedAt: raw.publishedAtUtc ?? raw.createdAtUtc ?? '',
  };
}

/**
 * Real line items from the PayrollSlip's own columns — the same breakdown the
 * backend uses for the PDF when a slip has no itemised component rows.
 */
function payslipLines(raw: any): { earnings: PayslipLine[]; deductions: PayslipLine[] } {
  const num = (v: unknown) => Number(v ?? 0) || 0;
  const earnings: PayslipLine[] = [
    { code: 'BASIC', description: 'Basic salary', descriptionAr: 'الراتب الأساسي', amount: num(raw.basicSalary), type: 'Earning' as const },
    { code: 'HOUSING', description: 'Housing allowance', descriptionAr: 'بدل السكن', amount: num(raw.housingAllowance), type: 'Earning' as const },
    { code: 'TRANSPORT', description: 'Transport allowance', descriptionAr: 'بدل النقل', amount: num(raw.transportAllowance), type: 'Earning' as const },
    { code: 'OTHER', description: 'Other allowances', descriptionAr: 'بدلات أخرى', amount: num(raw.otherAllowances), type: 'Earning' as const },
    { code: 'ARREARS', description: 'Arrears', descriptionAr: 'فروقات مستحقة', amount: num(raw.arrearsAmount), type: 'Earning' as const },
  ].filter((l) => l.amount !== 0);

  // Only the columns the slip itself carries. Whatever they do not explain stays unexplained: the
  // screen flags it as "not itemised" rather than inventing an "Other deductions" line by subtraction.
  const statutory = num(raw.employeeStatutoryTotal);
  const loans = num(raw.loanDeductions);
  const deductions: PayslipLine[] = [
    { code: 'STATUTORY', description: 'Statutory (GOSI)', descriptionAr: 'التأمينات الاجتماعية (GOSI)', amount: statutory, type: 'Deduction' as const },
    { code: 'LOAN', description: 'Loan / advance recovery', descriptionAr: 'استرداد قرض / سلفة', amount: loans, type: 'Deduction' as const },
  ].filter((l) => l.amount !== 0);

  return {
    earnings: earnings.map((l, i) => ({ ...l, id: `e${i}` })),
    deductions: deductions.map((l, i) => ({ ...l, id: `d${i}` })),
  };
}

function mapApprovalType(entityName?: string): ApprovalItem['type'] {
  const value = (entityName ?? '').toUpperCase();
  if (value.includes('LEAVE')) return 'LEAVE';
  if (value.includes('OVERTIME')) return 'OVERTIME';
  if (value.includes('ATTENDANCE') || value.includes('REGULARIZATION')) return 'ATTENDANCE_CORRECTION';
  if (value.includes('PAYROLL')) return 'PAYROLL';
  if (value.includes('REQUISITION') || value.includes('RECRUIT')) return 'RECRUITMENT_REQUISITION';
  if (value.includes('LOAN')) return 'LOAN';
  if (value.includes('ADVANCE')) return 'ADVANCE';
  if (value.includes('APPRAISAL')) return 'APPRAISAL';
  return 'HR_REQUEST';
}

function mapApprovalItem(item: any): ApprovalItem {
  const title: string = item.title ?? item.entityName ?? 'Approval request';
  // Titles are "<what> — <who>" (e.g. "Annual Leave — Raj Krishnamurthy").
  const [, requester] = title.split(' — ');
  const pending = String(item.status ?? 'Pending').toLowerCase() === 'pending';
  const canDecide = pending && item.canDecide !== false;
  const details: Record<string, string | number> = { status: item.status ?? 'Pending' };
  if (item.currentQueue) details.queue = item.currentQueue;
  if (item.priority) details.priority = item.priority;
  if (item.isOverdue) details.sla = 'Overdue';
  return {
    id: String(item.id ?? ''),
    taskId: String(item.id ?? ''),
    type: mapApprovalType(item.entityName ?? item.module),
    title,
    requestedBy: item.requestedByName ?? requester ?? 'Requester',
    // Never "just now" when the server sent no date: an empty string renders as no date.
    requestedAt: item.createdAtUtc ?? '',
    urgency: item.isOverdue ? 'HIGH' : String(item.priority ?? '').toLowerCase() === 'high' ? 'HIGH' : 'MEDIUM',
    summary: item.description ?? item.entityName ?? '',
    details,
    timeline: [],
    canApprove: canDecide,
    canReject: canDecide,
    // S5 send-back (stream W2-E): offered only once the backend route is live.
    canSendBack: FEATURES.APPROVAL_SEND_BACK && canDecide,
  };
}

function mapHRStatus(status?: string): HRRequest['status'] {
  const v = (status ?? '').replace(/[\s_-]/g, '').toLowerCase();
  if (v === 'inprogress' || v === 'assigned' || v === 'pendingemployee') return 'InProgress';
  if (v === 'resolved') return 'Resolved';
  if (v === 'closed') return 'Closed';
  if (v === 'cancelled' || v === 'canceled') return 'Cancelled';
  return 'Open';
}

function mapHRComment(c: any): HRRequestComment {
  return {
    id: String(c.id ?? ''),
    // HR replies are stored against the ticket's employee too; only Employee-authored rows are "mine".
    authorId: c.authorType !== 'HR' && c.employeeId != null ? String(c.employeeId) : undefined,
    authorName: c.authorName ?? (c.authorType === 'HR' ? 'HR' : 'You'),
    authorRole: c.authorType ?? 'Employee',
    message: c.comment ?? c.message ?? '',
    content: c.comment ?? c.message ?? '',
    createdAt: c.createdAtUtc ?? '',
  };
}

function mapHRRequest(raw: any, comments: any[] = []): HRRequest {
  return {
    id: String(raw.id ?? ''),
    requestType: (raw.categoryName ?? 'General') as HRRequestType,
    subject: raw.subject ?? '',
    description: raw.description ?? '',
    status: mapHRStatus(raw.status),
    slaDeadline: raw.dueAtUtc ?? undefined,
    slaBreached: !!raw.isOverdue,
    slaStatus: raw.isOverdue ? 'Breached' : 'OnTime',
    responseStatus: raw.responseStatus ?? undefined,
    commentsCount: comments.length || undefined,
    createdAt: raw.createdAtUtc ?? '',
    updatedAt: raw.updatedAtUtc ?? raw.createdAtUtc ?? '',
    comments: comments.map(mapHRComment),
  };
}

function mapOvertimeStatus(status?: string): OvertimeRequest['status'] {
  const v = (status ?? '').toLowerCase();
  if (v.includes('approved')) return 'Approved';
  if (v.includes('reject')) return 'Rejected';
  if (v.includes('cancel')) return 'Cancelled';
  return 'Pending'; // PendingManager, PendingHR, Submitted…
}

/** An overtime request with the raw instants and status, so screens format times on the company clock. */
export interface OvertimeRecord extends OvertimeRequest {
  startUtc: string;
  endUtc: string;
  rawStatus: string;
  decidedAt: string | null;
}

function mapOvertimeRequest(raw: any): OvertimeRecord {
  const minutes = Number(raw.requestedMinutes ?? 0);
  return {
    id: String(raw.id ?? ''),
    date: raw.workDate ?? '',
    startTime: utcIsoToLocalHHmm(raw.startTimeUtc),
    endTime: utcIsoToLocalHHmm(raw.endTimeUtc),
    startUtc: raw.startTimeUtc ?? '',
    endUtc: raw.endTimeUtc ?? '',
    totalHours: minutes / 60,
    durationMinutes: minutes,
    approvedMinutes: Number(raw.approvedMinutes ?? 0),
    reason: raw.reason ?? '',
    status: mapOvertimeStatus(raw.status),
    rawStatus: String(raw.status ?? ''),
    submittedAt: raw.createdAtUtc ?? '',
    decidedAt: raw.decidedAtUtc ?? null,
    approvalTimeline: [],
  };
}

function mapDocument(raw: any): EmployeeDocument {
  const days =
    raw.expiryDate != null
      ? Math.ceil((new Date(raw.expiryDate).getTime() - Date.now()) / 86_400_000)
      : undefined;
  const status: EmployeeDocument['status'] =
    days === undefined ? 'VALID' : days < 0 ? 'EXPIRED' : days <= 60 ? 'EXPIRING_SOON' : 'VALID';
  return {
    id: String(raw.id ?? ''),
    documentType: raw.documentType ?? 'Document',
    expiryDate: raw.expiryDate ?? undefined,
    status: raw.approvalStatus === 'Pending' ? 'PENDING_REVIEW' : status,
    verificationStatus: raw.approvalStatus ?? undefined,
    fileName: raw.fileName ?? undefined,
    uploadedAt: raw.uploadedAtUtc ?? undefined,
    documentNumber:
      typeof raw.notes === 'string' && raw.notes.startsWith('Document number: ')
        ? raw.notes.slice('Document number: '.length)
        : undefined,
    daysUntilExpiry: days,
  };
}

/** A file picked on the device, ready to be sent as a multipart part. */
export interface PickedFile {
  uri: string;
  name: string;
  mimeType: string;
  size?: number;
}

const MIME_BY_EXT: Record<string, string> = {
  pdf: 'application/pdf',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  png: 'image/png',
  heic: 'image/heic',
  heif: 'image/heic',
};

/** Pickers do not always report a MIME type; the backend requires one that matches the bytes. */
export function normalizePickedFile(file: { uri: string; name?: string | null; mimeType?: string | null; size?: number | null }): PickedFile {
  const fromUri = file.uri.split('?')[0].split('/').pop() ?? 'upload';
  const name = file.name || fromUri;
  const ext = (name.split('.').pop() ?? '').toLowerCase();
  const declared = (file.mimeType ?? '').toLowerCase();
  const mimeType = declared === 'image/jpg' ? 'image/jpeg' : declared || MIME_BY_EXT[ext] || 'application/octet-stream';
  return { uri: file.uri, name, mimeType, size: file.size ?? undefined };
}

/** React Native's FormData accepts { uri, name, type } for a file part. */
function filePart(file: PickedFile): any {
  return { uri: file.uri, name: file.name, type: file.mimeType };
}

const MULTIPART = { headers: { 'Content-Type': 'multipart/form-data' }, timeout: 120_000 };

/** Absolute URL for an API route such as "/api/ess/profile/photo?v=…" (the base URL ends in /api). */
function apiOrigin(): string {
  const base = String(getApiClient().defaults.baseURL ?? getApiBaseUrl());
  return base.replace(/\/api\/?$/, '');
}

export interface AuthenticatedSession {
  user: AuthUser;
  tokens: AuthTokens;
}

export type LoginOutcome =
  | ({ kind: 'authenticated' } & AuthenticatedSession)
  | {
      kind: 'mfaChallenge';
      challengeToken: string;
      expiresInSeconds: number;
      email: string;
    }
  | {
      kind: 'mfaEnrollment';
      enrollmentToken: string;
      expiresInSeconds: number;
      email: string;
    };

function authenticatedSession(data: any): AuthenticatedSession {
  if (!data?.accessToken || !data?.user) {
    throw new Error('Unexpected sign-in response from the server.');
  }
  const user = toAuthUser(data.user);
  // HR's live reset code may be reported on the reply or on the user; keep it on the user.
  if (!user.pendingResetNotice && data.pendingResetNotice?.date) user.pendingResetNotice = { date: String(data.pendingResetNotice.date) };
  return {
    user,
    tokens: {
      accessToken: data.accessToken,
      refreshToken: data.refreshToken,
      expiresAt: toTokenExpiry(data.expiresAtUtc),
    },
  };
}

// ---- Auth ----
export const authApi = {
  async login(username: string, password: string): Promise<LoginOutcome> {
    const input = publicLoginInput(username, password);
    const res = await createPublicAuthClient().post('/auth/login', input);
    const step = classifyLoginResponse(unwrapApiData<unknown>(res.data));
    if (step.kind === 'mfaChallenge' || step.kind === 'mfaEnrollment') {
      return { ...step, email: input.email };
    }
    return { kind: 'authenticated', ...authenticatedSession(step.payload) };
  },

  async verifyMfaChallenge(
    challengeToken: string,
    totpCode: string
  ): Promise<AuthenticatedSession> {
    const response = await createPublicAuthClient().post('/auth/mfa/challenge/verify', {
      challengeToken,
      totpCode,
    });
    return authenticatedSession(unwrapApiData<any>(response.data));
  },

  /** Exchanges a setup-only enrolment token for a fresh otpauth URI. Each call mints a new secret. */
  async startMfaEnrollment(
    enrollmentToken: string
  ): Promise<{ provisioningUri: string; tempSecret: string }> {
    const response = await createPublicAuthClient().post('/auth/mfa/enrollment/setup', {
      enrollmentToken,
    });
    const data = unwrapApiData<any>(response.data);
    const provisioningUri = String(data?.provisioningUri ?? '');
    const tempSecret = secretFromProvisioningUri(provisioningUri);
    if (!provisioningUri || !tempSecret) {
      throw new Error('The server did not return a valid two-step setup key.');
    }
    return { provisioningUri, tempSecret };
  },

  /**
   * Turns the factor on. No session is issued; the user signs in again with a code.
   * Returns one-time recovery codes only if the server sends them (tenant enrolment
   * answers 204 today, so this is null).
   */
  async verifyMfaEnrollment(
    enrollmentToken: string,
    tempSecret: string,
    totpCode: string
  ): Promise<{ recoveryCodes: string[] | null }> {
    const response = await createPublicAuthClient().post('/auth/mfa/enrollment/verify-setup', {
      enrollmentToken,
      tempSecret,
      totpCode,
    });
    return { recoveryCodes: parseRecoveryCodes(unwrapApiData<unknown>(response?.data)) };
  },

  /** Signed-in standing against mandatory two-step sign-in (grace prompt). */
  async getMfaStatus(): Promise<MfaPrompt> {
    const status = await apiGet<unknown>('/auth/mfa/status');
    return classifyMfaStatus(status, Date.now());
  },

  /**
   * Starts enrolment from a signed-in session: the backend hands back the same
   * setup-only token the sign-in flow uses. Completing it ends this session.
   */
  async startSignedInMfaEnrollment(): Promise<{ enrollmentToken: string; expiresInSeconds: number }> {
    const data = await apiPost<any>('/auth/mfa/enrollment/start');
    const enrollmentToken = typeof data?.enrollmentToken === 'string' ? data.enrollmentToken : '';
    if (!enrollmentToken) throw new Error('The server did not return a two-step setup request.');
    return { enrollmentToken, expiresInSeconds: normalizeTtl(data?.expiresInSeconds) };
  },

  async logout(refreshToken: string): Promise<void> {
    await apiPost('/auth/logout', { refreshToken });
  },

  async getMe(): Promise<AuthUser> {
    const user = await apiGet<any>('/auth/me');
    return toAuthUser(user);
  },

  async changePassword(oldPassword: string, newPassword: string): Promise<void> {
    await apiPost('/auth/change-password', { currentPassword: oldPassword, newPassword });
  },

  /**
   * Resolves to `{ emailDelivery: false }` when the reply says no email can be sent for this
   * company (then only HR's welcome code helps); otherwise the reply says nothing either way.
   */
  async forgotPassword(email: string): Promise<{ emailDelivery?: boolean }> {
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedEmail) throw new Error('Work email is required.');
    const res = await createPublicAuthClient().post('/auth/forgot-password', { email: normalizedEmail });
    const data = unwrapApiData<Record<string, unknown> | undefined>(res?.data);
    const noDelivery = data?.emailDeliveryConfigured === false || data?.emailed === false || data?.emailSent === false;
    return noDelivery ? { emailDelivery: false } : {};
  },

  /**
   * First sign-in: exchange HR's welcome code for a password the employee chooses. 200 and NO
   * session; the caller signs in next. Refusals are 400 `{ code }` (see auth/welcomeCode.ts
   * welcomeErrorKey) or 429.
   */
  async welcomeRedeem(email: string, code: string, newPassword: string): Promise<void> {
    const normalizedEmail = normalizeEmail(email);
    if (!normalizedEmail) throw new Error('Work email is required.');
    const res = await createPublicAuthClient().post('/auth/welcome/redeem', {
      email: normalizedEmail,
      code: normalizeWelcomeCode(code),
      newPassword,
    });
  },

  /** The company's minimum password length for the live tick; 10 when unknown or unreachable. */
  async passwordPolicy(): Promise<number> {
    try {
      const res = await createPublicAuthClient().get('/auth/password-policy');
      const n = Number(unwrapApiData<{ minLength?: number }>(res.data)?.minLength);
      return Number.isInteger(n) && n >= 1 && n <= 128 ? n : DEFAULT_MIN_PASSWORD_LENGTH;
    } catch {
      return DEFAULT_MIN_PASSWORD_LENGTH;
    }
  },

  async resetPassword(resetToken: string, newPassword: string): Promise<void> {
    await createPublicAuthClient().post('/auth/reset-password', publicResetInput(resetToken, newPassword));
  },

  /** First-time password setup from an invitation (backend: /auth/accept-invitation). */
  async acceptInvitation(invitationToken: string, newPassword: string): Promise<void> {
    await createPublicAuthClient().post('/auth/accept-invitation', publicInvitationInput(invitationToken, newPassword));
  },
};

// ---- Device Registration ----
export const deviceApi = {
  async register(payload: {
    deviceId: string;
    pushToken?: string;
    platform: string;
    model: string;
    osVersion: string;
    appVersion: string;
  }): Promise<void> {
    const employeeId = await getCurrentEmployeeId();
    if (!employeeId) return;
    // The backend ignores employeeId and registers against the caller (IDOR-safe);
    // it is still sent because the DTO declares it.
    await apiPost('/mobile/register-device', {
      employeeId,
      deviceIdentifier: payload.deviceId,
      platform: payload.platform,
      pushToken: payload.pushToken,
    });
  },

  async unregister(deviceId: string): Promise<void> {
    await apiDelete(`/mobile/register-device/${encodeURIComponent(deviceId)}`);
  },
};

/**
 * Today's attendance for the signed-in employee. The processed daily record is
 * authoritative, but it only exists after HR runs attendance processing — so a
 * punch made a minute ago is visible only as a raw event. Without this, the
 * dashboard kept offering "Clock In" after a successful clock-in and never
 * offered "Clock Out".
 */
async function resolveTodayAttendance(dailyRecord: any | null | undefined): Promise<TodayAttendance> {
  if (dailyRecord && (dailyRecord.firstInUtc || dailyRecord.lastOutUtc)) {
    return {
      status: mapStatus(dailyRecord.status),
      clockIn: dailyRecord.firstInUtc ?? undefined,
      clockOut: dailyRecord.lastOutUtc ?? undefined,
      currentlyActive: !!dailyRecord.firstInUtc && !dailyRecord.lastOutUtc,
    };
  }
  const employeeId = await getCurrentEmployeeId();
  if (!employeeId) return fallbackTodayAttendance;
  const today = riyadhBusinessDate();
  const raw = await apiGet<BackendPaged<any>>(
    `/attendance/events/raw?from=${today}&to=${today}&employeeId=${employeeId}&pageSize=100`
  );
  const events = itemsOf(raw)
    .filter((e) => e.punchTimestampUtc)
    .sort((a, b) => String(a.punchTimestampUtc).localeCompare(String(b.punchTimestampUtc)));
  if (events.length === 0) {
    return dailyRecord ? { status: mapStatus(dailyRecord.status), currentlyActive: false } : fallbackTodayAttendance;
  }
  const firstIn = events.find((e) => e.punchDirection === 'In');
  const last = events[events.length - 1];
  const lastOut = [...events].reverse().find((e) => e.punchDirection === 'Out');
  const active = last.punchDirection === 'In';
  return {
    status: 'PRESENT',
    clockIn: firstIn?.punchTimestampUtc,
    clockOut: active ? undefined : lastOut?.punchTimestampUtc,
    currentlyActive: active,
  };
}

// ---- Dashboard ----
export const dashboardApi = {
  /**
   * GET /ess/dashboard. Also carries what the shared type leaves out (announcements, today's
   * processed status and worked minutes — see mapDashboardExtras). Unread notifications here are
   * the dashboard's top five; Home replaces the count with the full unread list when it loads.
   */
  async getEmployeeDashboard(): Promise<EmployeeDashboard & EmployeeHomeExtras> {
    const data = await apiGet<any>('/ess/dashboard');
    const todayAttendance = await resolveTodayAttendance(data.attendanceToday).catch(
      () => fallbackTodayAttendance
    );
    return {
      profile: data.profile
        ? {
            employeeId: String(data.profile.employeeId ?? ''),
            fullName: data.profile.fullName ?? 'Employee',
            jobTitle: data.profile.jobTitle ?? undefined,
            department: data.profile.department ?? undefined,
            profilePhotoUrl: data.profile.profilePhotoUrl || undefined,
          }
        : undefined,
      todayAttendance,
      leaveBalances: (data.leaveBalances ?? []).map(mapLeaveBalance),
      pendingRequestsCount: Number(data.pendingRequests ?? 0),
      expiringDocuments: (data.documentAlerts ?? []).map(mapDocument),
      unreadNotifications: (data.notifications ?? []).filter((n: any) => !n.isRead).length,
      // Loaded separately (holidaysApi) so a missing calendar never fails the dashboard.
      upcomingHolidays: [],
      ...mapDashboardExtras(data),
    };
  },

  /**
   * Manager summary built from the SAME sources and definitions as the Approvals and Team screens
   * (features/approvals/approvalModel + teamModel), so every number on Home reconciles with the list
   * it opens. /dashboard/overview is not used: its approval count came from a different query than
   * the queue, and its team figures are tenant-wide.
   */
  async getManagerDashboard(): Promise<ManagerDashboard> {
    const [inbox, team] = await Promise.all([approvalsApi.getInbox(), teamApi.getTeamToday()]);
    const counts = teamTodayCounts(team.members, team.day);
    const pendingByType: Partial<Record<ApprovalItem['type'], number>> = {};
    for (const item of inbox.items) {
      const key = mapApprovalType(item.kind === 'RECRUITMENT' ? 'REQUISITION' : item.kind);
      pendingByType[key] = (pendingByType[key] ?? 0) + 1;
    }
    return {
      teamSummary: {
        total: counts.total,
        present: counts.present,
        absent: counts.absent,
        onLeave: counts.onLeave,
        lateToday: counts.late,
      },
      pendingApprovalsCount: inbox.items.length,
      pendingByType: pendingByType as ManagerDashboard['pendingByType'],
      // Overtime hours are no longer summed here: the old figure mixed pending and approved hours
      // and opened the pending queue. Pending overtime is counted in the approvals inbox instead.
      overtimeAlert: undefined,
    };
  },

  /** The tenant's public holidays in a calendar year (GET /leave/holidays/range). */
  async getHolidays(year: number): Promise<Holiday[]> {
    const rows = await holidaysApi.getRange(`${year}-01-01`, `${year}-12-31`);
    return rows.map((h) => ({
      date: h.date,
      name: h.nameEn,
      nameAr: h.nameAr,
      type: /relig|islam|eid/i.test(h.type ?? '') ? 'RELIGIOUS' : /company/i.test(h.type ?? '') ? 'COMPANY' : 'PUBLIC',
    }));
  },
};

// ---- Attendance ----
/** The punch pre-check for a login with no employee link, carrying the server's code so it maps to plain words. */
async function requirePunchEmployee(): Promise<void> {
  if (await getCurrentEmployeeId()) return;
  throw Object.assign(new Error('Your login is not linked to an employee record. Please contact HR.'), { code: 'employee_not_linked' });
}

function punchDirection(punchType: MobilePunchPayload['punchType']): PunchDirection {
  return punchType === 'CLOCK_OUT' || punchType === 'BREAK_OUT' ? 'Out' : 'In';
}

// ---- Selfie attendance v2 (discovery, consent, evidence) ----
// Every call acts on the caller's own linked employee; none takes an employee id.

let verificationCache: VerificationCacheEntry | null = null;

async function verificationCacheKey(): Promise<string> {
  const user = await userStorage.getUser();
  return `${user?.tenantId ?? ''}:${user?.employeeId ?? ''}:${user?.id ?? ''}`;
}

async function storeVerification(raw: unknown): Promise<AttendanceVerification> {
  const value = parseAttendanceVerification(raw);
  verificationCache = { key: await verificationCacheKey(), at: Date.now(), value };
  return value;
}

export const selfieAttendanceApi = {
  /** GET /ess/attendance-verification, cached in memory for a minute (JSON only; images are never cached). */
  async getVerification(options: { force?: boolean } = {}): Promise<AttendanceVerification> {
    const key = await verificationCacheKey();
    if (!options.force && cacheIsFresh(verificationCache, key, Date.now())) return verificationCache!.value;
    return storeVerification(await apiGet<unknown>('/ess/attendance-verification'));
  },

  /** The last answer for this sign-in, if still fresh; lets a screen render without a spinner. */
  async peekVerification(): Promise<AttendanceVerification | null> {
    const key = await verificationCacheKey();
    return cacheIsFresh(verificationCache, key, Date.now()) ? verificationCache!.value : null;
  },

  clearVerificationCache(): void {
    verificationCache = null;
  },

  /** POST /ess/biometric-consent. 201 new, 200 already recorded; both answer the discovery body. */
  async giveConsent(policyVersion: string): Promise<AttendanceVerification> {
    return storeVerification(await apiPost<unknown>('/ess/biometric-consent', { policyVersion, channel: 'Mobile' }));
  },

  /**
   * POST /ess/biometric-consent/withdraw. Always allowed, idempotent; answers the discovery body plus a
   * `withdrawal` block (unused selfies deleted now, and any still awaiting deletion by the purge).
   */
  async withdrawConsent(): Promise<{ verification: AttendanceVerification; withdrawal: WithdrawalResult | null }> {
    const raw = await apiPost<unknown>('/ess/biometric-consent/withdraw', { channel: 'Mobile' });
    return { verification: await storeVerification(raw), withdrawal: parseWithdrawal(raw) };
  },

  /**
   * POST /attendance/evidence/selfie (multipart, one `file` part, JPEG only, request ≤ 8 MB). The server re-encodes
   * the image and strips EXIF/GPS; the app sends the camera file as captured. Returns the opaque,
   * single-use evidence id (10 minutes, this employee only).
   */
  async uploadSelfie(uri: string): Promise<{ evidenceId: string; expiresAtUtc: string }> {
    const send = () => {
      const form = new FormData();
      form.append('file', filePart({ uri, name: 'attendance-selfie.jpg', mimeType: 'image/jpeg' }));
      return apiPost<any>('/attendance/evidence/selfie', form, {
        ...MULTIPART,
        headers: { ...MULTIPART.headers, ...clientPlatformHeaders(Platform.OS) },
        timeout: 60_000,
      });
    };
    let result: any;
    try {
      result = await send();
    } catch (error) {
      // The server answers a fast 429 when its image processing is busy: retry once after its Retry-After
      // (1–5 s), then let the caller show "Selfie processing is busy". The hourly limit is not retried.
      if (!isSelfieBusy(error)) throw error;
      await new Promise((resolve) => setTimeout(resolve, busyRetryDelayMs(error)));
      result = await send();
    }
    const evidenceId = String(result?.evidenceId ?? '');
    if (!evidenceId) throw new Error('The selfie upload returned no evidence id.');
    return { evidenceId, expiresAtUtc: String(result?.expiresAtUtc ?? '') };
  },
};

export const attendanceApi = {
  /**
   * POST /attendance/punch/mobile for the signed-in employee only (employeeId 0: the server resolves
   * the caller's own link). Sends latitude, longitude, accuracy and the mocked flag for the server-side
   * geofence, and the selfie evidence id when there is one. No verification claim is sent: the server
   * decides what was verified.
   */
  async punch(payload: MobilePunchPayload): Promise<{ recordId: string; message: string; verificationMethod: string }> {
    await requirePunchEmployee();
    const direction = punchDirection(payload.punchType);
    const result = await apiPost<any>('/attendance/punch/mobile', buildPunchBody({
      direction,
      locationName: payload.location ? 'Mobile GPS' : 'Mobile',
      location: buildLocationFields(payload.location ? { coords: payload.location, mocked: payload.location.mocked } : null, Platform.OS),
      evidenceId: payload.evidenceId,
    }), { headers: clientPlatformHeaders(Platform.OS) });
    return { recordId: String(result?.id ?? ''), message: `${direction} punch recorded`, verificationMethod: String(result?.verificationMethod ?? 'None') };
  },

  /**
   * Kiosk route remains authenticated and is always called for the signed-in employee. Never carries a
   * selfie. A caller without the kiosk permission gets the mobile rules here (location accuracy, mock
   * detection, and a selfie where required), so the same location fields and platform header are sent.
   */
  async punchKiosk(payload: MobilePunchPayload): Promise<{ recordId: string; message: string }> {
    await requirePunchEmployee();
    const direction = punchDirection(payload.punchType);
    const result = await apiPost<any>('/attendance/punch/kiosk', buildPunchBody({
      direction,
      locationName: payload.location ? 'KynexOne Kiosk GPS' : 'KynexOne Kiosk',
      location: buildLocationFields(payload.location ? { coords: payload.location, mocked: payload.location.mocked } : null, Platform.OS),
    }), { headers: clientPlatformHeaders(Platform.OS) });
    return { recordId: String(result?.id ?? ''), message: `${direction} punch recorded` };
  },

  /** Caller-scoped raw events make a just-recorded kiosk punch immediately visible. */
  async getKioskTodayAttendance(): Promise<TodayAttendance> {
    return resolveTodayAttendance(null);
  },

  async getTodayAttendance(): Promise<TodayAttendance> {
    const dashboard = await apiGet<any>('/ess/dashboard');
    return resolveTodayAttendance(dashboard.attendanceToday);
  },

  /**
   * Per-day records for a month. /attendance/monthly is a per-employee
   * *aggregate* (presentDays, absentDays…), not a day list — reading it as days
   * rendered a wall of ABSENT. /attendance/daily is the per-day source; for an
   * employee it is scoped server-side to their own record.
   */
  async getMonthlyAttendance(year: number, month: number): Promise<AttendanceDay[]> {
    const employeeId = await requireEmployeeId();
    const from = localIsoDate(new Date(year, month - 1, 1));
    const to = localIsoDate(new Date(year, month, 0));
    const result = await apiGet<BackendPaged<any>>(
      `/attendance/daily?from=${from}&to=${to}&employeeId=${employeeId}&page=1&pageSize=62`
    );
    return itemsOf(result)
      .map(
        (record: any): AttendanceDay => ({
          date: record.workDate,
          // Missing punch = one punch without the other. No punches at all keeps the server's verdict
          // (Absent, Rest day, On leave…), which the processor also flags as missingPunch.
          status: record.missingPunch && !!record.firstInUtc !== !!record.lastOutUtc ? 'MISSING_PUNCH' : mapStatus(record.status),
          clockIn: record.firstInUtc ?? undefined,
          clockOut: record.lastOutUtc ?? undefined,
          workHours: Number(record.totalWorkedMinutes ?? 0) / 60,
          lateMinutes: Number(record.lateMinutes ?? 0),
          earlyExitMinutes: Number(record.earlyExitMinutes ?? 0),
          hasCorrection: !!record.manualCorrectionStatus && record.manualCorrectionStatus !== 'None',
        })
      )
      .sort((a, b) => b.date.localeCompare(a.date));
  },

  /** Requested times are company wall-clock times (Asia/Riyadh), not the phone's zone. */
  async submitRegularization(payload: {
    date: string;
    requestedClockIn?: string;
    requestedClockOut?: string;
    reason: string;
  }): Promise<AttendanceRegularization> {
    const result = await apiPost<any>('/ess/attendance/regularization', {
      workDate: payload.date,
      requestType: 'MissingPunch',
      requestedInUtc: payload.requestedClockIn
        ? companyWallTimeToUtcIso(payload.date, payload.requestedClockIn)
        : undefined,
      requestedOutUtc: payload.requestedClockOut
        ? companyWallTimeToUtcIso(payload.date, payload.requestedClockOut)
        : undefined,
      reason: payload.reason,
    });
    return {
      id: String(result.id ?? ''),
      date: result.workDate ?? payload.date,
      reason: result.reason ?? payload.reason,
      requestedClockIn: result.requestedInUtc,
      requestedClockOut: result.requestedOutUtc,
      status: 'PENDING',
      createdAt: result.createdAtUtc ?? new Date().toISOString(),
    };
  },

  async getMyRegularizations(): Promise<AttendanceRegularization[]> {
    const result = await apiGet<BackendPaged<any>>('/attendance/regularization/my');
    return itemsOf(result).map((item: any) => ({
      id: String(item.id ?? ''),
      date: item.workDate ?? '',
      reason: item.reason ?? '',
      requestedClockIn: item.requestedInUtc,
      requestedClockOut: item.requestedOutUtc,
      status: mapLeaveStatus(item.status) as AttendanceRegularization['status'],
      createdAt: item.createdAtUtc ?? '',
    }));
  },
};

// ---- Leave ----
export const leaveApi = {
  async getLeaveBalances(): Promise<LeaveBalance[]> {
    const employeeId = await getCurrentEmployeeId();
    const url = employeeId ? `/leave/balances/employee/${employeeId}` : '/ess/leave/balance';
    const balances = await apiGet<any[]>(url);
    return balances.map(mapLeaveBalance);
  },

  async getLeaveTypes(): Promise<{ id: string; name: string; nameAr?: string; isHalfDayAllowed?: boolean }[]> {
    const types = await apiGet<any[]>('/leave/types');
    return types
      .filter((type) => type.isActive !== false)
      .map((type) => ({
        id: String(type.id),
        name: type.nameEn ?? type.name ?? 'Leave',
        nameAr: type.nameAr,
        isHalfDayAllowed: type.isHalfDayAllowed,
      }));
  },

  async submitLeaveRequest(payload: {
    leaveTypeId: string;
    startDate: string;
    endDate: string;
    isHalfDay?: boolean;
    halfDayPeriod?: 'MORNING' | 'AFTERNOON';
    reason?: string;
    /** Id of an EmployeeDocument uploaded first via documentsApi.uploadDocument. */
    attachmentDocumentId?: string;
  }): Promise<LeaveRequest> {
    const employeeId = await requireEmployeeId();
    const result = await apiPost<any>('/leave/requests', {
      employeeId,
      leaveTypeId: payload.leaveTypeId,
      startDate: payload.startDate,
      // A half day is one date; the server refuses a half day over a range.
      endDate: payload.isHalfDay ? payload.startDate : payload.endDate,
      dayType: payload.isHalfDay ? 'Half' : 'Full',
      reason: payload.reason,
      isEmergency: false,
      // The file is uploaded first (POST /ess/documents); only its document id is
      // sent here and the server resolves the storage key. Never file bytes.
      attachmentDocumentId: payload.attachmentDocumentId || undefined,
    });
    return mapLeaveRequest(result);
  },

  async getMyLeaveRequests(status?: string): Promise<LeaveRequest[]> {
    const employeeId = await getCurrentEmployeeId();
    const params = new URLSearchParams({ page: '1', pageSize: '25' });
    if (status) params.set('status', status);
    if (employeeId) params.set('employeeId', String(employeeId));
    const result = await apiGet<BackendPaged<any>>(`/leave/requests?${params.toString()}`);
    return itemsOf(result).map(mapLeaveRequest);
  },

  async cancelLeave(id: string): Promise<void> {
    await apiPost(`/leave/requests/${id}/cancel`, { reason: 'Cancelled from mobile app' });
  },
};

// ---- Overtime ----
/** The limits of the tenant's overtime policy, as the server applies them (OvertimePolicyLimits). */
export interface OvertimePolicyInfo {
  id: string;
  minimumMinutes: number;
  maximumMinutesPerDay: number;
  monthlyCapMinutes: number;
  roundingRule: string;
}

/** What payroll calculated for an approved request (OvertimeController /calculations). */
export interface OvertimePay {
  overtimeRequestId: string;
  approvedMinutes: number;
  multiplier: number;
  amount: number;
  currency: string;
}

export const overtimeApi = {
  /**
   * Backend DTO (OvertimeController.cs):
   *   OvertimeRequestCreate(int EmployeeId, Guid? OvertimePolicyId, Guid? OvertimeTypeId,
   *                         DateOnly WorkDate, DateTime StartTimeUtc, DateTime EndTimeUtc,
   *                         string? Source, string? Reason)
   * Times are wall-clock on the company calendar (`timeZone`, default the device zone for old
   * callers). Crossing midnight must be asked for with `endsNextDay`; an end at or before the start
   * is refused here instead of being silently pushed into the next day.
   */
  async submitOTRequest(payload: {
    date: string;
    startTime: string;
    endTime: string;
    reason: string;
    endsNextDay?: boolean;
    timeZone?: string;
    overtimeTypeId?: string;
  }): Promise<OvertimeRecord> {
    const employeeId = await requireEmployeeId();
    const nextDay = Boolean(payload.endsNextDay);
    let startIso: string;
    let endIso: string;
    if (payload.timeZone) {
      startIso = zonedWallTimeToUtcIso(payload.date, payload.startTime, payload.timeZone);
      endIso = zonedWallTimeToUtcIso(payload.date, payload.endTime, payload.timeZone, nextDay ? 1 : 0);
    } else {
      startIso = localDateTimeToUtcIso(payload.date, payload.startTime);
      const end = new Date(localDateTimeToUtcIso(payload.date, payload.endTime));
      endIso = new Date(end.getTime() + (nextDay ? 86_400_000 : 0)).toISOString();
    }
    if (new Date(endIso).getTime() <= new Date(startIso).getTime()) {
      throw new Error('The end time must be after the start time.');
    }
    const result = await apiPost<any>('/overtime/requests', {
      employeeId,
      workDate: payload.date,
      startTimeUtc: startIso,
      endTimeUtc: endIso,
      reason: payload.reason,
      overtimeTypeId: payload.overtimeTypeId || undefined,
      source: 'Mobile',
    });
    return mapOvertimeRequest(result);
  },

  /**
   * GET /overtime/requests is scoped server-side; for an employee it returns only their own.
   * Every page of the employee's history: this used to read one page of 50 and show it as the
   * whole history, so the 51st request and everything older simply was not there.
   */
  async getMyOTRequests(): Promise<OvertimeRecord[]> {
    const employeeId = await requireEmployeeId();
    const rows = await fetchAllPages<any>((page, pageSize) =>
      apiGet<BackendPaged<any>>(`/overtime/requests?employeeId=${employeeId}&page=${page}&pageSize=${pageSize}`)
    );
    return rows.map(mapOvertimeRequest);
  },

  /**
   * GET /overtime/policies. The server applies the FIRST active policy when a request names none, and
   * that order is not defined — so limits are returned only when exactly one policy is active.
   * null means "unknown": the form then checks only the 12-hour ceiling.
   */
  async getPolicyLimits(): Promise<OvertimePolicyInfo | null> {
    const policies = await apiGet<any[]>('/overtime/policies');
    const active = (policies ?? []).filter((p) => p.isActive !== false && !p.isDeleted);
    if (active.length !== 1) return null;
    const p = active[0];
    return {
      id: String(p.id),
      minimumMinutes: Number(p.minimumMinutes ?? 0),
      maximumMinutesPerDay: Number(p.maximumMinutesPerDay ?? 0),
      monthlyCapMinutes: Number(p.monthlyCapMinutes ?? 0),
      roundingRule: String(p.roundingRule ?? ''),
    };
  },

  /**
   * GET /overtime/calculations?employeeId={self} — pay worked out for approved requests. The endpoint
   * is not pageable: it returns the newest 100. An older request simply shows no pay line (never a guess).
   */
  async getMyPay(): Promise<OvertimePay[]> {
    const employeeId = await requireEmployeeId();
    const rows = await apiGet<any[]>(`/overtime/calculations?employeeId=${employeeId}`);
    return (rows ?? []).map((r) => ({
      overtimeRequestId: String(r.overtimeRequestId ?? ''),
      approvedMinutes: Number(r.approvedMinutes ?? 0),
      multiplier: Number(r.multiplier ?? 0),
      amount: Number(r.amount ?? 0),
      currency: String(r.currency ?? ''),
    }));
  },
};

/** Headers for file downloads that bypass axios (FileSystem.downloadAsync). */
async function authHeaders(): Promise<Record<string, string>> {
  const token = await tokenStorage.getAccessToken();
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  return headers;
}

// ---- Payslips ----
/** A payslip as the detail screen shows it. */
export interface PayslipView extends PayslipDetail {
  /** Employer contributions (GOSI employer share, hazard) — paid on top of pay, not deducted from it. */
  employerLines: PayslipLine[];
  /** The server's own check that the lines add up to the totals; null when it did not say. */
  reconciled: boolean | null;
}

async function payslipCurrency(): Promise<string | undefined> {
  try {
    const dashboard = await apiGet<any>('/ess/dashboard');
    return dashboard?.payrollSnapshot?.currency ?? undefined;
  } catch {
    return undefined;
  }
}

export const payslipApi = {
  async getPayslips(): Promise<Payslip[]> {
    const slips = await apiGet<any[]>('/ess/payslips');
    // The list carries its own currency now; ask the dashboard only for an older backend.
    const currency = slips.length > 0 && !slips[0].currency ? await payslipCurrency() : undefined;
    return slips.map((s) => mapPayslip(s, currency));
  },

  /**
   * GET /ess/payslips/{id} (W2-D S6): the slip's real component lines, built the
   * way the PDF builds them, with header totals computed from those lines.
   * Lines carry nameAr; employer contributions (paid on top, never deducted) are kept apart.
   * Falls back to the list row's columns on an older backend (404).
   */
  async getPayslipDetail(id: string): Promise<PayslipView> {
    try {
      const d = await apiGet<any>(`/ess/payslips/${id}`);
      const lines: any[] = d.lines ?? [];
      const toLine = (l: any, i: number, prefix: string, type: PayslipLine['type']): PayslipLine => ({
        id: `${prefix}${i}`,
        code: String(l.name ?? '').toUpperCase().replace(/[^A-Z0-9]+/g, '_'),
        description: l.name ?? '',
        descriptionAr: l.nameAr || undefined,
        componentName: l.name ?? '',
        amount: Number(l.amount ?? 0),
        type,
      });
      const earnings = lines.filter((l) => l.type === 'Earning').map((l, i) => toLine(l, i, 'e', 'Earning'));
      const deductions = lines.filter((l) => l.type === 'Deduction').map((l, i) => toLine(l, i, 'd', 'Deduction'));
      const employerLines = lines
        .filter((l) => l.type === 'EmployerContribution')
        .map((l, i) => toLine(l, i, 'c', 'Deduction'));
      const base = mapPayslip({ ...d, status: d.status ?? 'Final' });
      return {
        ...base,
        grossSalary: Number(d.grossSalary ?? 0),
        grossPay: Number(d.grossSalary ?? 0),
        totalDeductions: Number(d.totalDeductions ?? 0),
        netSalary: Number(d.netSalary ?? 0),
        netPay: Number(d.netSalary ?? 0),
        earnings,
        deductions,
        details: [...earnings, ...deductions],
        employerLines,
        reconciled: typeof d.reconciled === 'boolean' ? d.reconciled : null,
        ytdGross: d.ytdGross ?? undefined,
        ytdNet: d.ytdNet ?? undefined,
      };
    } catch (e: any) {
      if (e?.response?.status !== 404) throw e;
    }
    const [slips, currency] = await Promise.all([apiGet<any[]>('/ess/payslips'), payslipCurrency()]);
    const raw = slips.find((item) => String(item.id) === id);
    if (!raw) throw Object.assign(new Error('payslip_not_found'), { response: { status: 404 } });
    const { earnings, deductions } = payslipLines(raw);
    return {
      ...mapPayslip(raw, currency),
      earnings,
      deductions,
      details: [...earnings, ...deductions],
      employerLines: [],
      reconciled: null,
      loanDeductions: Number(raw.loanDeductions ?? 0) || undefined,
      ytdGross: raw.ytdGross ?? undefined,
      ytdNet: raw.ytdNet ?? undefined,
    };
  },

  /** GET /ess/payslips/{id}/download streams a PDF; it needs the bearer token and tenant header. */
  async downloadPayslip(id: string): Promise<{ url: string; headers: Record<string, string> }> {
    return { url: `${getApiClient().defaults.baseURL}/ess/payslips/${id}/download`, headers: await authHeaders() };
  },
};

// ---- Profile ----
export const profileApi = {
  /**
   * GET /ess/profile, plus the employing company's country from /auth/me (companies[].countryCode)
   * so the screen shows that jurisdiction's identity documents. Country stays unknown when /auth/me
   * fails or the session spans companies in different countries.
   */
  async getProfile(): Promise<EmployeeProfileRecord> {
    const [profile, me] = await Promise.all([
      apiGet<any>('/ess/profile'),
      apiGet<any>('/auth/me').catch(() => null),
    ]);
    const companies: any[] = Array.isArray(me?.companies) ? me.companies : [];
    const countries = new Set(companies.map((c) => String(c?.countryCode ?? '').trim().toUpperCase()).filter(Boolean));
    return {
      ...mapEmployeeProfile(profile),
      companyCountry: countries.size === 1 ? [...countries][0] : undefined,
    };
  },

  async requestProfileUpdate(changes: Record<string, string | undefined>): Promise<void> {
    const cleaned = Object.fromEntries(Object.entries(changes).filter(([, v]) => v !== undefined && v !== ''));
    await apiPut('/ess/profile-change-request', {
      changes: cleaned,
      reason: 'Requested from mobile app',
    });
  },

  /**
   * POST /ess/profile/photo (multipart). The server re-encodes to a ≤512px JPEG
   * with EXIF stripped and answers { photoUrl: "/api/ess/profile/photo?v=…" }.
   */
  async uploadProfilePhoto(file: PickedFile): Promise<{ photoUrl: string }> {
    if (!FEATURES.PROFILE_PHOTO_UPLOAD) throw new FeatureUnavailableError('Profile photo upload');
    if (file.mimeType === 'image/heic') {
      throw new Error('HEIC photos are not supported. Choose a JPEG or PNG photo.');
    }
    const form = new FormData();
    form.append('file', filePart(file));
    return apiPost<{ photoUrl: string }>('/ess/profile/photo', form, MULTIPART);
  },

  /** An <Image source> for a profile photo route: needs the bearer token, like any API call. */
  async photoSource(photoUrl?: string | null): Promise<{ uri: string; headers: Record<string, string> } | null> {
    if (!photoUrl) return null;
    if (photoUrl.startsWith('/api/')) {
      return { uri: `${apiOrigin()}${photoUrl}`, headers: await authHeaders() };
    }
    if (/^https:\/\//i.test(photoUrl)) {
      return { uri: photoUrl, headers: {} };
    }
    return null;
  },
};

// ---- Documents ----
export const documentsApi = {
  async getDocuments(): Promise<EmployeeDocument[]> {
    const docs = await apiGet<any[]>('/ess/documents');
    return docs.map(mapDocument);
  },

  /**
   * POST /ess/documents (multipart, W2-D S1). PDF, JPEG, PNG or HEIC up to 10 MB;
   * the server checks the declared type, the extension and the file's bytes, and
   * generates the storage key itself. The response never contains that key.
   */
  async uploadDocument(payload: {
    file: PickedFile;
    documentType: string;
    expiryDate?: string;
    documentNumber?: string;
  }): Promise<EmployeeDocument> {
    if (!FEATURES.FILE_UPLOAD) throw new FeatureUnavailableError('Document upload');
    if (payload.file.size && payload.file.size > 10 * 1024 * 1024) {
      throw new Error('The file is larger than 10 MB.');
    }
    const form = new FormData();
    form.append('file', filePart(payload.file));
    form.append('documentType', payload.documentType);
    if (payload.expiryDate) form.append('expiryDate', payload.expiryDate);
    if (payload.documentNumber) form.append('documentNumber', payload.documentNumber);
    const created = await apiPost<any>('/ess/documents', form, MULTIPART);
    return mapDocument(created);
  },

  /** GET /ess/documents/{id}/download — own documents only (a colleague's id is 404). */
  async downloadDocument(id: string): Promise<{ url: string; headers: Record<string, string> }> {
    return { url: `${getApiClient().defaults.baseURL}/ess/documents/${id}/download`, headers: await authHeaders() };
  },
};

// ---- Policies ----
export const policiesApi = {
  async getPolicies(): Promise<{ id: string; title: string; status: string }[]> {
    const docs = await apiGet<any[]>('/ess/policies');
    return docs.map((d) => ({ id: String(d.id), title: d.fileName ?? d.documentType, status: d.approvalStatus }));
  },

  async acknowledgePolicy(id: string): Promise<void> {
    await apiPost(`/ess/policies/${id}/acknowledge`);
  },
};

// ---- HR Requests ----
export const hrRequestsApi = {
  async createRequest(payload: {
    requestType: string;
    subject: string;
    description: string;
    /** Id of an EmployeeDocument the caller uploaded first (documentsApi.uploadDocument). */
    attachmentDocumentId?: string;
  }): Promise<HRRequest> {
    const created = await apiPost<any>('/ess/hr-requests', {
      categoryName: payload.requestType,
      subject: payload.subject,
      description: payload.description,
      priority: 'Normal',
      attachmentDocumentId: payload.attachmentDocumentId || undefined,
    });
    return mapHRRequest(created);
  },

  async getMyRequests(): Promise<HRRequest[]> {
    const items = await apiGet<any[]>('/ess/hr-requests/my');
    return items.map((r) => mapHRRequest(r));
  },

  /** GET /ess/hr-requests/{id} → { request, comments, hrResponded, isOverdue, responseStatus } */
  async getRequestDetail(id: string): Promise<HRRequest> {
    const data = await apiGet<any>(`/ess/hr-requests/${id}`);
    return mapHRRequest(
      { ...data.request, isOverdue: data.isOverdue, responseStatus: data.responseStatus },
      data.comments ?? []
    );
  },

  async addComment(id: string, message: string): Promise<void> {
    await apiPost(`/ess/hr-requests/${id}/comments`, { comment: message });
  },
};

// ---- Approvals ----
export interface ApprovalOutcome {
  /** The request's status AFTER this decision: Pending (moved on), Approved, Rejected, ReturnedToRequester. */
  status: string;
  /** True only when the whole request is finished, not just this approver's step. */
  isFinal: boolean;
  currentStepOrder?: number;
  /** e.g. "Role:HR Manager" — who has it now, when it is still pending. */
  currentQueue?: string;
  /** Human-readable next approver, from currentQueue. */
  nextApprover?: string;
}

function toApprovalOutcome(r: any, fallbackStatus: string): ApprovalOutcome {
  const status = String(r?.status ?? fallbackStatus);
  const queue: string | undefined = r?.currentQueue || undefined;
  const nextApprover = queue ? queue.replace(/^(Role|User|Group|Position):/i, '').trim() || undefined : undefined;
  return {
    status,
    isFinal: status.toLowerCase() !== 'pending',
    currentStepOrder: r?.currentStepOrder ?? undefined,
    currentQueue: queue,
    nextApprover,
  };
}

/** One field of a pending employee change: on file now → proposed (already masked by the server). */
export type EmployeeChangeField = {
  key: string;
  label: string;
  currentValue: string | null;
  proposedValue: string | null;
  masked: boolean;
};

export const approvalsApi = {
  async getPendingApprovals(): Promise<ApprovalItem[]> {
    // The whole pending queue, page by page: an approver used to see only the first 50.
    const pending = await fetchAllPages<any>((page, pageSize) =>
      apiGet<BackendPaged<any>>(`/approval-requests?status=Pending&page=${page}&pageSize=${pageSize}`)
    );
    return pending.map(mapApprovalItem);
  },

  async getApprovalHistory(page = 1, pageSize = 20): Promise<PaginatedResponse<ApprovalItem>> {
    const result = await apiGet<BackendPaged<any>>(`/approval-requests?page=${page}&pageSize=${pageSize}`);
    const items = itemsOf(result).filter((i: any) => String(i.status ?? '').toLowerCase() !== 'pending');
    return {
      data: items.map(mapApprovalItem),
      total: result.total ?? items.length,
      page: result.page ?? page,
      pageSize: result.pageSize ?? pageSize,
      hasMore: (result.total ?? 0) > page * pageSize,
    };
  },

  /**
   * Backend: ApprovalDecisionRequest(Decision: "Approve" | "Reject", Comments?).
   *
   * Multi-step approvals run in steps (F1): a 200 on Approve does NOT mean the
   * request is approved. Approving step 1 of 2 answers
   * { status: "Pending", currentStepOrder: 2, currentQueue: "Role:HR Manager" }.
   * The caller reads `status`. `canDecide` in this response is always false (a
   * known backend issue) and is deliberately not used.
   */
  async approve(taskId: string, comment?: string): Promise<ApprovalOutcome> {
    const r = await apiPost<any>(`/approval-requests/${taskId}/decisions`, { decision: 'Approve', comments: comment });
    return toApprovalOutcome(r, 'Approved');
  },

  async reject(taskId: string, reason: string): Promise<ApprovalOutcome> {
    const r = await apiPost<any>(`/approval-requests/${taskId}/decisions`, { decision: 'Reject', comments: reason });
    return toApprovalOutcome(r, 'Rejected');
  },

  /**
   * S5 (stream W2-E): POST /approval-requests/{id}/send-back { comments } with the
   * same authorization as /decisions; 1–1000 chars of comments are required so
   * the requester knows what to fix. Behind FEATURES.APPROVAL_SEND_BACK (off).
   */
  async sendBack(taskId: string, reason: string): Promise<ApprovalOutcome> {
    if (!FEATURES.APPROVAL_SEND_BACK) throw new FeatureUnavailableError('Send back');
    const comments = reason.trim();
    if (comments.length < 1 || comments.length > 1000) {
      throw new Error('Please explain what needs to change (up to 1000 characters).');
    }
    const r = await apiPost<any>(`/approval-requests/${taskId}/send-back`, { comments });
    return toApprovalOutcome(r, 'ReturnedToRequester');
  },

  async getApprovalDetail(taskId: string): Promise<ApprovalItem> {
    const item = await apiGet<any>(`/approval-requests/${taskId}`);
    return mapApprovalItem(item);
  },

  /**
   * Everything waiting for this manager, in one exception-first list (approvalModel.mergeInbox):
   *   - workflow approvals where the caller is the current approver (server-scoped), every page;
   *   - overtime at the manager stage (PendingManager) for the caller's scoped team, every page —
   *     OvertimeController has its own approval path, so these are never in /approval-requests.
   * If one source fails the other is still shown and `unavailable` names the missing one, so a
   * partial list is never presented as complete.
   */
  async getInbox(): Promise<{ items: InboxItem[]; unavailable: ('workflow' | 'overtime')[] }> {
    const self = Number(await getCurrentEmployeeId());
    const selfId = Number.isFinite(self) && self > 0 ? self : null;
    const [workflow, overtime] = await Promise.allSettled([
      fetchAllPages<any>((page, pageSize) =>
        apiGet<BackendPaged<any>>(`/approval-requests?status=Pending&page=${page}&pageSize=${pageSize}`)
      ),
      fetchAllPages<any>((page, pageSize) =>
        apiGet<BackendPaged<any>>(`/overtime/requests?status=PendingManager&page=${page}&pageSize=${pageSize}`)
      ),
    ]);
    if (workflow.status === 'rejected' && overtime.status === 'rejected') throw workflow.reason;
    const unavailable: ('workflow' | 'overtime')[] = [];
    const workflowItems = workflow.status === 'fulfilled' ? workflow.value.map(mapWorkflowApproval) : [];
    if (workflow.status === 'rejected') unavailable.push('workflow');
    let overtimeItems: InboxItem[] = [];
    if (overtime.status === 'fulfilled') {
      const scoped = overtime.value
        .map((row) => mapOvertimeApproval(row, selfId))
        .filter((x): x is InboxItem => x !== null);
      // The scope includes indirect reports the caller cannot approve (the server checks the
      // employee's resolved approvers), so ask the server who approves each employee's overtime.
      const isAdmin = (await userStorage.getUser())?.role === 'SUPER_ADMIN';
      const approverOf = new Map<number, boolean | null>();
      if (!isAdmin && selfId !== null) {
        const employeeIds = [...new Set(scoped.map((i) => i.requesterEmployeeId).filter((id): id is number => id !== null))];
        await Promise.all(employeeIds.map(async (employeeId) => {
          try {
            const resolved = await apiGet<any>(`/employees/${employeeId}/workflow-approvers?workflowType=Overtime`);
            const approvers: any[] = Array.isArray(resolved?.approvers) ? resolved.approvers : [];
            approverOf.set(employeeId, approvers.some((a) => Number(a?.employeeId) === selfId));
          } catch {
            approverOf.set(employeeId, null);
          }
        }));
      }
      const decidable = decidableOvertime(scoped, approverOf, isAdmin);
      overtimeItems = decidable.items;
      if (decidable.unresolved) unavailable.push('overtime');
    } else if (overtime.reason?.response?.status !== 403) {
      // 403 = this login has no overtime approval rights: nothing is missing, there is nothing to show.
      unavailable.push('overtime');
    }
    return { items: mergeInbox(workflowItems, overtimeItems), unavailable };
  },

  /**
   * Requests on the caller's team that the caller decided (GET /approval-requests?queue=team, one
   * status at a time — the queue accepts a single status), reduced to rows carrying a decision by
   * this user. /approval-requests without a queue returns only rows waiting for the caller, so
   * history read from it was always empty.
   */
  async getMyDecisions(myUserId: string): Promise<MyDecision[]> {
    const statuses = ['Approved', 'Rejected', 'Pending', 'ReturnedToRequester'];
    const pages = await Promise.all(
      statuses.map((status) =>
        fetchAllPages<any>((page, pageSize) =>
          apiGet<BackendPaged<any>>(`/approval-requests?queue=team&status=${status}&page=${page}&pageSize=${pageSize}`)
        )
      )
    );
    return myDecisionsFrom(pages.flat(), myUserId);
  },

  /**
   * What a manager needs before deciding a leave request: the request itself, the requester's
   * balance for that leave type, and approved leave by others on the same dates. Balance and
   * overlap are best-effort: when either cannot be read it is null and the sheet says so.
   */
  /** GET /api/leave/requests/{id} — dates, days and reason for a leave approval card. */
  async getLeaveFacts(leaveRequestId: string): Promise<LeaveFacts | null> {
    return mapLeaveFacts(await apiGet<any>(`/leave/requests/${leaveRequestId}`));
  },

  /**
   * GET /approval-requests/{id}/change-detail — before → after for an employee change, per field. Answered only
   * to someone who may decide the request now (404 otherwise); identity and bank numbers arrive masked to their
   * last 4, pay masked unless the viewer has pay access. Values are shown exactly as the server sends them.
   */
  async getEmployeeChangeDetail(approvalId: string): Promise<EmployeeChangeField[]> {
    const r = await apiGet<any>(`/approval-requests/${approvalId}/change-detail`);
    const fields: any[] = Array.isArray(r?.fields) ? r.fields : [];
    return fields.map((x) => ({
      key: String(x?.key ?? ''),
      label: String(x?.label ?? x?.key ?? ''),
      currentValue: x?.currentValue == null ? null : String(x.currentValue),
      proposedValue: x?.proposedValue == null ? null : String(x.proposedValue),
      masked: x?.masked === true,
    }));
  },

  async getLeaveContext(leaveRequestId: string): Promise<{
    leave: LeaveFacts;
    balance: BalanceFacts | null;
    balanceUnavailable: boolean;
    overlap: OutEntry[] | null;
  }> {
    const leave = await approvalsApi.getLeaveFacts(leaveRequestId);
    if (!leave) throw new Error('Leave request not found.');
    const [balances, calendar] = await Promise.allSettled([
      leave.employeeId
        ? apiGet<BackendPaged<any>>(`/leave/balances?employeeId=${leave.employeeId}`)
        : Promise.reject(new Error('No employee')),
      leave.startDate && leave.endDate
        ? teamCalendarApi.getTeamLeave(leave.startDate, leave.endDate)
        : Promise.reject(new Error('No dates')),
    ]);
    const balance = balances.status === 'fulfilled' ? balanceFacts(itemsOf(balances.value), leave) : null;
    return {
      leave,
      balance,
      balanceUnavailable: balances.status === 'rejected',
      overlap:
        calendar.status === 'fulfilled' && leave.startDate && leave.endDate
          ? overlapping(calendar.value, leave.startDate, leave.endDate, leave.employeeId)
          : null,
    };
  },

  /** Approve or reject one inbox item through its own source's endpoint; returns the TRUE resulting state. */
  async decide(item: InboxItem, decision: 'approve' | 'reject', comment?: string): Promise<DecisionOutcome> {
    const notes = comment?.trim() || undefined;
    if (item.source === 'overtime') {
      // OvertimeDecisionRequest(int ApprovedMinutes, string? Notes): a manager approves the hours as requested.
      const r = await apiPost<any>(`/overtime/requests/${item.id}/${decision}`, {
        approvedMinutes: decision === 'approve' ? item.overtime?.minutes ?? 0 : 0,
        notes,
      });
      return overtimeOutcome(r);
    }
    const r = await apiPost<any>(`/approval-requests/${item.id}/decisions`, {
      decision: decision === 'approve' ? 'Approve' : 'Reject',
      comments: notes,
    });
    return workflowOutcome(r);
  },
};

// ---- Notifications ----
export const notificationsApi = {
  async getNotifications(unreadOnly = false): Promise<AppNotification[]> {
    const employeeId = await getCurrentEmployeeId();
    if (!employeeId) return [];
    const items = await apiGet<any[]>(`/mobile/notifications/${employeeId}${unreadOnly ? '?unreadOnly=true' : ''}`);
    return items.map((item) => ({
      id: String(item.id ?? ''),
      type: 'SYSTEM',
      title: item.title ?? item.subject ?? 'Notification',
      body: item.body ?? item.message ?? '',
      isRead: !!item.isRead,
      createdAt: item.createdAtUtc ?? new Date().toISOString(),
      actionRoute: item.actionRoute,
      actionId: item.actionId,
    }));
  },

  async markRead(id: string): Promise<void> {
    await apiPost(`/mobile/notifications/${id}/read`);
  },

  async markAllRead(): Promise<void> {
    const notifications = await this.getNotifications(true);
    await Promise.allSettled(notifications.map((notification) => this.markRead(notification.id)));
  },

  /**
   * GET /ess/notification-preferences (W2-D S4):
   * { push: { approvals: { enabled, locked }, … }, email: {…}, sms: {…} }.
   * Absent server rows read as enabled; locked categories cannot be turned off.
   */
  async getPreferences(): Promise<NotificationPreferences> {
    if (!FEATURES.NOTIFICATION_PREFERENCES) throw new FeatureUnavailableError('Notification preferences');
    return apiGet<NotificationPreferences>('/ess/notification-preferences');
  },

  /** Partial update: { push: { leave: false } }. Returns the full, updated view. */
  async updatePreferences(changes: Partial<Record<NotificationChannelKey, Record<string, boolean>>>): Promise<NotificationPreferences> {
    if (!FEATURES.NOTIFICATION_PREFERENCES) throw new FeatureUnavailableError('Notification preferences');
    return apiPut<NotificationPreferences>('/ess/notification-preferences', changes);
  },

  /**
   * The channel master switch (GET/PUT /notifications/preferences). Push is OFF
   * until the employee turns it on: a missing server row is not consent.
   */
  async getChannelSwitches(): Promise<ChannelSwitches> {
    return apiGet<ChannelSwitches>('/notifications/preferences');
  },

  async setPushEnabled(enabled: boolean): Promise<ChannelSwitches> {
    const current = await apiGet<ChannelSwitches>('/notifications/preferences');
    return apiPut<ChannelSwitches>('/notifications/preferences', { ...current, pushEnabled: enabled });
  },
};

export type NotificationChannelKey = 'push' | 'email' | 'sms';
export type NotificationPreferences = Record<NotificationChannelKey, Record<string, { enabled: boolean; locked: boolean }>>;
export interface ChannelSwitches {
  emailEnabled: boolean;
  pushEnabled: boolean;
  smsEnabled: boolean;
  quietHoursJson: string;
}

// ---- AI Assistant ----
export const aiApi = {
  /** Backend DTO: ESSAIQuestionDto(string Question) → ESSAIAnswerDto(string Answer). */
  async ask(payload: AIAskPayload): Promise<AIAskResponse> {
    const data = await apiPost<{ answer: string }>('/ess/ai/ask', { question: payload.question });
    return { answer: data.answer };
  },
};

// ---- Team (Manager/Supervisor) ----
export const teamApi = {
  /**
   * Direct reports with today's status, plus what is known about today itself: whether it is a
   * public holiday in the tenant's calendar (so nobody is counted absent on it). The holiday check
   * is best-effort; when it fails `holidayKnown` is false and no one is counted as off for it.
   */
  async getTeamToday(): Promise<{
    members: TeamMemberToday[];
    day: TeamDay;
    holiday: { nameEn: string | null; nameAr: string | null } | null;
    holidayKnown: boolean;
    businessDate: string;
  }> {
    const businessDate = riyadhBusinessDate();
    const [members, holiday] = await Promise.all([
      teamApi.getTeamMembers(),
      teamCalendarApi.getPublicHoliday(businessDate).then(
        (h) => ({ ok: true as const, h }),
        () => ({ ok: false as const, h: null })
      ),
    ]);
    return {
      members,
      day: { publicHoliday: holiday.h !== null },
      holiday: holiday.h,
      holidayKnown: holiday.ok,
      businessDate,
    };
  },

  /**
   * GET /ess/team (W2-D S8): the caller's direct reports with today's status,
   * resolved server-side from the daily record, then today's raw punches, then
   * approved leave. Falls back to the scoped /employees + /attendance/daily
   * derivation on an older backend (404).
   */
  async getTeamMembers(): Promise<TeamMemberToday[]> {
    try {
      const team = await apiGet<any[]>('/ess/team');
      return team.map(
        (m): TeamMemberToday => ({
          // The server's own words ("Not clocked in", "Late", …): teamModel buckets these, so a
          // report with no punch yet is "not clocked in", not "absent", before the day is processed.
          rawStatus: m.todayStatus ?? '',
          currentlyActive: m.currentlyActive === true,
          employeeId: String(m.employeeId),
          fullName: m.fullName ?? 'Employee',
          jobTitle: m.jobTitle ?? '',
          department: m.department ?? undefined,
          profilePhotoUrl: m.profilePhotoUrl || undefined,
          todayStatus: mapStatus(m.todayStatus),
          clockIn: m.clockInUtc ?? undefined,
          clockOut: m.clockOutUtc ?? undefined,
        })
      );
    } catch (e: any) {
      if (e?.response?.status !== 404) throw e;
    }
    const employeeId = await getCurrentEmployeeId();
    if (!employeeId) return [];
    const today = riyadhBusinessDate();
    // Both lists in full: /employees clamps pageSize to 100, so asking for 200 returned the first
    // 100 employees and a manager's reports past them never reached the filter below.
    const [employees, attendance] = await Promise.all([
      fetchAllPages<any>((page, pageSize) => apiGet<BackendPaged<any>>(`/employees?page=${page}&pageSize=${pageSize}`)),
      fetchAllPages<any>((page, pageSize) =>
        apiGet<BackendPaged<any>>(`/attendance/daily?from=${today}&to=${today}&page=${page}&pageSize=${pageSize}`)
      ).catch(() => null),
    ]);
    const byEmployee = new Map(itemsOf(attendance).map((a: any) => [a.employeeId, a]));
    return itemsOf(employees)
      .filter((e: any) => e.managerEmployeeId === employeeId && e.id !== employeeId)
      .map((e: any): TeamMemberToday => {
        const a = byEmployee.get(e.id);
        return {
          rawStatus: a ? (a.missingPunch ? 'Missing punch' : String(a.status ?? '')) : 'Not clocked in',
          currentlyActive: Boolean(a?.firstInUtc && !a?.lastOutUtc),
          employeeId: String(e.id),
          fullName: e.fullName ?? 'Employee',
          jobTitle: e.designation ?? '',
          department: e.department,
          profilePhotoUrl: e.profilePhotoUrl
            ? `/api/employees/${e.id}/photo`
            : undefined,
          todayStatus: a ? (a.missingPunch ? 'MISSING_PUNCH' : mapStatus(a.status)) : 'ABSENT',
          clockIn: a?.firstInUtc ?? undefined,
          clockOut: a?.lastOutUtc ?? undefined,
        };
      });
  },
};
