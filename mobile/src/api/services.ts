// ============================================================
// KynexOne Mobile — API Services
// ============================================================
//
// Every request and response shape here was checked against the backend on the
// `develop` branch (backend-dotnet/Zayra.Api) and exercised against a live API.
// Where the backend has no endpoint for a feature, the function throws
// FeatureUnavailableError and the screen hides/disables the control
// (see src/config/features.ts).

import { apiDelete, apiGet, apiPost, apiPut, createApiClient, getApiClient, unwrapApiData } from './client';
import { tokenStorage, userStorage, appStorage } from '@/storage';
import { APP_CONFIG } from '@/config';
import { FEATURES, FeatureUnavailableError } from '@/config/features';
import { normalizeAccessMode } from '@/auth/accessPolicy';
import { mapEmployeeProfile } from './profileMapper';
import { riyadhBusinessDate, riyadhBusinessMonth } from '@/utils/businessDate';
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
  EmployeeProfile,
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
  TeamMember,
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

/** HH:mm in the device's local time for a UTC ISO timestamp. */
function utcIsoToLocalHHmm(value?: string | null): string {
  if (!value) return '';
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return '';
  return `${String(d.getHours()).padStart(2, '0')}:${String(d.getMinutes()).padStart(2, '0')}`;
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
    periodLabel:
      raw.periodLabel ||
      (hasPeriod
        ? new Date(year, month - 1, 1).toLocaleDateString('en-US', { month: 'long', year: 'numeric' })
        : 'Payslip'),
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
    { code: 'BASIC', description: 'Basic Salary', amount: num(raw.basicSalary), type: 'Earning' as const },
    { code: 'HOUSING', description: 'Housing Allowance', amount: num(raw.housingAllowance), type: 'Earning' as const },
    { code: 'TRANSPORT', description: 'Transport Allowance', amount: num(raw.transportAllowance), type: 'Earning' as const },
    { code: 'OTHER', description: 'Other Allowances', amount: num(raw.otherAllowances), type: 'Earning' as const },
    { code: 'ARREARS', description: 'Arrears', amount: num(raw.arrearsAmount), type: 'Earning' as const },
  ].filter((l) => l.amount !== 0);

  const total = num(raw.deductions);
  const statutory = num(raw.employeeStatutoryTotal);
  const loans = num(raw.loanDeductions);
  const other = Math.round((total - statutory - loans) * 100) / 100;
  const deductions: PayslipLine[] = [
    { code: 'STATUTORY', description: 'Statutory (GOSI)', amount: statutory, type: 'Deduction' as const },
    { code: 'LOAN', description: 'Loan / Advance Recovery', amount: loans, type: 'Deduction' as const },
    { code: 'OTHER_DED', description: 'Other Deductions', amount: other > 0 ? other : 0, type: 'Deduction' as const },
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
    requestedAt: item.createdAtUtc ?? new Date().toISOString(),
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

function mapOvertimeRequest(raw: any): OvertimeRequest {
  const minutes = Number(raw.requestedMinutes ?? 0);
  return {
    id: String(raw.id ?? ''),
    date: raw.workDate ?? '',
    startTime: utcIsoToLocalHHmm(raw.startTimeUtc),
    endTime: utcIsoToLocalHHmm(raw.endTimeUtc),
    totalHours: minutes / 60,
    durationMinutes: minutes,
    approvedMinutes: Number(raw.approvedMinutes ?? 0),
    reason: raw.reason ?? '',
    status: mapOvertimeStatus(raw.status),
    submittedAt: raw.createdAtUtc ?? '',
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
  const base = String(getApiClient().defaults.baseURL ?? APP_CONFIG.API_BASE_URL);
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
      tenantId: string;
      email: string;
    }
  | {
      kind: 'mfaEnrollment';
      enrollmentToken: string;
      expiresInSeconds: number;
      tenantId: string;
      email: string;
      message?: string;
    };

function authenticatedSession(data: any): AuthenticatedSession {
  if (!data?.accessToken || !data?.user) {
    throw new Error('Unexpected sign-in response from the server.');
  }
  return {
    user: toAuthUser(data.user),
    tokens: {
      accessToken: data.accessToken,
      refreshToken: data.refreshToken,
      expiresAt: toTokenExpiry(data.expiresAtUtc),
    },
  };
}

function secretFromProvisioningUri(provisioningUri: string): string {
  try {
    return new URL(provisioningUri).searchParams.get('secret') ?? '';
  } catch {
    return decodeURIComponent(provisioningUri.match(/[?&]secret=([^&]+)/i)?.[1] ?? '');
  }
}

// ---- Auth ----
export const authApi = {
  async login(username: string, password: string, tenantId: string): Promise<LoginOutcome> {
    const tempClient = createApiClient(tenantId);
    const res = await tempClient.post('/auth/login', {
      email: username,
      password,
      tenantSlug: tenantId,
    });
    const data = unwrapApiData<any>(res.data);
    if (data?.mfaRequired) {
      return {
        kind: 'mfaChallenge',
        challengeToken: String(data.challengeToken ?? ''),
        expiresInSeconds: Number(data.expiresInSeconds ?? 300),
        tenantId,
        email: username,
      };
    }
    if (data?.mfaEnrollmentRequired) {
      return {
        kind: 'mfaEnrollment',
        enrollmentToken: String(data.enrollmentToken ?? ''),
        expiresInSeconds: Number(data.expiresInSeconds ?? 600),
        tenantId,
        email: username,
        message: data.message,
      };
    }
    return { kind: 'authenticated', ...authenticatedSession(data) };
  },

  async verifyMfaChallenge(
    challengeToken: string,
    totpCode: string,
    tenantId: string
  ): Promise<AuthenticatedSession> {
    const response = await createApiClient(tenantId).post('/auth/mfa/challenge/verify', {
      challengeToken,
      totpCode,
    });
    return authenticatedSession(unwrapApiData<any>(response.data));
  },

  async startMfaEnrollment(
    enrollmentToken: string,
    tenantId: string
  ): Promise<{ provisioningUri: string; tempSecret: string }> {
    const response = await createApiClient(tenantId).post('/auth/mfa/enrollment/setup', {
      enrollmentToken,
    });
    const data = unwrapApiData<any>(response.data);
    const provisioningUri = String(data?.provisioningUri ?? '');
    const tempSecret = secretFromProvisioningUri(provisioningUri);
    if (!provisioningUri || !tempSecret) {
      throw new Error('The server did not return a valid MFA setup secret.');
    }
    return { provisioningUri, tempSecret };
  },

  async verifyMfaEnrollment(
    enrollmentToken: string,
    tempSecret: string,
    totpCode: string,
    tenantId: string
  ): Promise<void> {
    await createApiClient(tenantId).post('/auth/mfa/enrollment/verify-setup', {
      enrollmentToken,
      tempSecret,
      totpCode,
    });
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

  /** Unauthenticated: uses a throwaway client so it works from the login screen. */
  async forgotPassword(email: string, tenantSlug?: string): Promise<void> {
    await createApiClient(tenantSlug ?? '').post('/auth/forgot-password', {
      email,
      tenantSlug: tenantSlug || undefined,
    });
  },

  async resetPassword(email: string, resetToken: string, newPassword: string, tenantSlug?: string): Promise<void> {
    await createApiClient(tenantSlug ?? '').post('/auth/reset-password', {
      email,
      resetToken,
      newPassword,
      tenantSlug: tenantSlug || undefined,
    });
  },

  /** First-time password setup from an invitation (backend: /auth/accept-invitation). */
  async setupFirstPassword(
    email: string,
    invitationToken: string,
    newPassword: string,
    tenantSlug?: string
  ): Promise<void> {
    await createApiClient(tenantSlug ?? '').post('/auth/accept-invitation', {
      email,
      invitationToken,
      newPassword,
      tenantSlug: tenantSlug || undefined,
    });
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
  async getEmployeeDashboard(): Promise<EmployeeDashboard> {
    const data = await apiGet<any>('/ess/dashboard');
    const todayAttendance = await resolveTodayAttendance(data.attendanceToday).catch(
      () => fallbackTodayAttendance
    );
    return {
      todayAttendance,
      leaveBalances: (data.leaveBalances ?? []).map(mapLeaveBalance),
      pendingRequestsCount: Number(data.pendingRequests ?? 0),
      expiringDocuments: (data.documentAlerts ?? []).map(mapDocument),
      unreadNotifications: (data.notifications ?? []).filter((n: any) => !n.isRead).length,
      upcomingHolidays: [],
    };
  },

  async getManagerDashboard(): Promise<ManagerDashboard> {
    const [overview, team, overtime] = await Promise.all([
      apiGet<any>('/dashboard/overview'),
      teamApi.getTeamMembers(),
      apiGet<BackendPaged<any>>('/overtime/requests?page=1&pageSize=200').catch(() => null),
    ]);
    // Team counts come from the manager's own scoped team, not /dashboard/summary,
    // which reports tenant-wide headcount.
    const count = (s: string) => team.filter((m) => m.todayStatus === s).length;
    const monthPrefix = riyadhBusinessMonth();
    const otMinutes = itemsOf(overtime)
      .filter((r) => String(r.workDate ?? '').startsWith(monthPrefix) && !/reject/i.test(r.status ?? ''))
      .reduce((sum, r) => sum + Number(r.requestedMinutes ?? 0), 0);
    return {
      teamSummary: {
        total: team.length,
        present: count('PRESENT') + count('LATE'),
        absent: count('ABSENT'),
        onLeave: count('ON_LEAVE'),
        lateToday: count('LATE'),
      },
      pendingApprovalsCount: Number(overview.pendingApprovals ?? 0),
      pendingByType: (overview.approvalQueue ?? []).reduce((acc: Record<string, number>, item: any) => {
        const key = mapApprovalType(item.module ?? item.entityName);
        acc[key] = (acc[key] ?? 0) + 1;
        return acc;
      }, {}) as ManagerDashboard['pendingByType'],
      overtimeAlert: otMinutes > 0 ? { thisMonth: Math.round((otMinutes / 60) * 10) / 10, lastMonth: 0 } : undefined,
    };
  },

  async getHolidays(_year: number): Promise<Holiday[]> {
    // Holidays live under /leave/holidays/calendars/{id}/holidays; no screen uses this yet.
    return [];
  },
};

// ---- Attendance ----
export const attendanceApi = {
  async punch(payload: MobilePunchPayload): Promise<{ recordId: string; message: string }> {
    const employeeId = await requireEmployeeId();
    const direction = payload.punchType === 'CLOCK_OUT' || payload.punchType === 'BREAK_OUT' ? 'Out' : 'In';
    const result = await apiPost<any>('/attendance/punch/mobile', {
      employeeId,
      punchDirection: direction,
      locationName: payload.location ? 'Mobile GPS' : 'Mobile',
      latitude: payload.location?.latitude,
      longitude: payload.location?.longitude,
    });
    return { recordId: String(result.id ?? ''), message: `${direction} punch recorded` };
  },

  /** Kiosk route remains authenticated and is always called for the signed-in employee. */
  async punchKiosk(payload: MobilePunchPayload): Promise<{ recordId: string; message: string }> {
    const employeeId = await requireEmployeeId();
    const direction = payload.punchType === 'CLOCK_OUT' || payload.punchType === 'BREAK_OUT' ? 'Out' : 'In';
    const result = await apiPost<any>('/attendance/punch/kiosk', {
      employeeId,
      punchDirection: direction,
      locationName: payload.location ? 'KynexOne Kiosk GPS' : 'KynexOne Kiosk',
      latitude: payload.location?.latitude,
      longitude: payload.location?.longitude,
    });
    return { recordId: String(result.id ?? ''), message: `${direction} punch recorded` };
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
          status: record.missingPunch ? 'MISSING_PUNCH' : mapStatus(record.status),
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
        ? localDateTimeToUtcIso(payload.date, payload.requestedClockIn)
        : undefined,
      requestedOutUtc: payload.requestedClockOut
        ? localDateTimeToUtcIso(payload.date, payload.requestedClockOut)
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
      endDate: payload.endDate,
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
export const overtimeApi = {
  /**
   * Backend DTO (OvertimeController.cs, develop):
   *   OvertimeRequestCreate(int EmployeeId, Guid? OvertimePolicyId, Guid? OvertimeTypeId,
   *                         DateOnly WorkDate, DateTime StartTimeUtc, DateTime EndTimeUtc,
   *                         string? Source, string? Reason)
   * projectCode / costCenter / attachment / comp-off do not exist server-side.
   */
  async submitOTRequest(payload: {
    date: string;
    startTime: string;
    endTime: string;
    reason: string;
    overtimeTypeId?: string;
  }): Promise<OvertimeRequest> {
    const employeeId = await requireEmployeeId();
    const start = new Date(localDateTimeToUtcIso(payload.date, payload.startTime));
    let end = new Date(localDateTimeToUtcIso(payload.date, payload.endTime));
    // 22:00 → 02:00 is an overnight block, not a negative one.
    if (end.getTime() <= start.getTime()) end = new Date(end.getTime() + 24 * 60 * 60 * 1000);
    const result = await apiPost<any>('/overtime/requests', {
      employeeId,
      workDate: payload.date,
      startTimeUtc: start.toISOString(),
      endTimeUtc: end.toISOString(),
      reason: payload.reason,
      overtimeTypeId: payload.overtimeTypeId || undefined,
      source: 'Mobile',
    });
    return mapOvertimeRequest(result);
  },

  /** GET /overtime/requests is scoped server-side; for an employee it returns only their own. */
  async getMyOTRequests(): Promise<OvertimeRequest[]> {
    const employeeId = await requireEmployeeId();
    const result = await apiGet<BackendPaged<any>>(
      `/overtime/requests?employeeId=${employeeId}&page=1&pageSize=50`
    );
    return itemsOf(result).map(mapOvertimeRequest);
  },
};

/** Headers for file downloads that bypass axios (FileSystem.downloadAsync). */
async function authHeaders(): Promise<Record<string, string>> {
  const token = await tokenStorage.getAccessToken();
  const tenant = await appStorage.get<string>('zayra_tenant_id');
  const headers: Record<string, string> = {};
  if (token) headers.Authorization = `Bearer ${token}`;
  if (tenant) headers[APP_CONFIG.TENANT_HEADER] = tenant;
  return headers;
}

// ---- Payslips ----
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
   * Falls back to the list row's columns on an older backend (404).
   */
  async getPayslipDetail(id: string): Promise<PayslipDetail> {
    try {
      const d = await apiGet<any>(`/ess/payslips/${id}`);
      const lines: any[] = d.lines ?? [];
      const toLine = (l: any, i: number, prefix: string): PayslipLine => ({
        id: `${prefix}${i}`,
        code: String(l.name ?? '').toUpperCase().replace(/[^A-Z0-9]+/g, '_'),
        description: l.name ?? '',
        componentName: l.name ?? '',
        amount: Number(l.amount ?? 0),
        type: l.type === 'Deduction' ? 'Deduction' : 'Earning',
      });
      const earnings = lines.filter((l) => l.type === 'Earning').map((l, i) => toLine(l, i, 'e'));
      const deductions = lines.filter((l) => l.type === 'Deduction').map((l, i) => toLine(l, i, 'd'));
      const base = mapPayslip({ ...d, status: 'Final' });
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
        ytdGross: d.ytdGross ?? undefined,
        ytdNet: d.ytdNet ?? undefined,
      };
    } catch (e: any) {
      if (e?.response?.status !== 404) throw e;
    }
    const [slips, currency] = await Promise.all([apiGet<any[]>('/ess/payslips'), payslipCurrency()]);
    const raw = slips.find((item) => String(item.id) === id);
    if (!raw) throw new Error('This payslip is no longer available.');
    const { earnings, deductions } = payslipLines(raw);
    return {
      ...mapPayslip(raw, currency),
      earnings,
      deductions,
      details: [...earnings, ...deductions],
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
  async getProfile(): Promise<EmployeeProfile> {
    const profile = await apiGet<any>('/ess/profile');
    return mapEmployeeProfile(profile);
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
    if (!photoUrl || !photoUrl.startsWith('/api/')) return null;
    return { uri: `${apiOrigin()}${photoUrl}`, headers: await authHeaders() };
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

export const approvalsApi = {
  async getPendingApprovals(): Promise<ApprovalItem[]> {
    const result = await apiGet<BackendPaged<any>>('/approval-requests?status=Pending&page=1&pageSize=50');
    return itemsOf(result).map(mapApprovalItem);
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
   * GET /ess/team (W2-D S8): the caller's direct reports with today's status,
   * resolved server-side from the daily record, then today's raw punches, then
   * approved leave. Falls back to the scoped /employees + /attendance/daily
   * derivation on an older backend (404).
   */
  async getTeamMembers(): Promise<TeamMember[]> {
    try {
      const team = await apiGet<any[]>('/ess/team');
      return team.map(
        (m): TeamMember => ({
          employeeId: String(m.employeeId),
          fullName: m.fullName ?? 'Employee',
          jobTitle: m.jobTitle ?? '',
          department: m.department ?? undefined,
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
    const [employees, attendance] = await Promise.all([
      apiGet<BackendPaged<any>>('/employees?page=1&pageSize=200'),
      apiGet<BackendPaged<any>>(`/attendance/daily?from=${today}&to=${today}&pageSize=200`).catch(() => null),
    ]);
    const byEmployee = new Map(itemsOf(attendance).map((a: any) => [a.employeeId, a]));
    return itemsOf(employees)
      .filter((e: any) => e.managerEmployeeId === employeeId && e.id !== employeeId)
      .map((e: any): TeamMember => {
        const a = byEmployee.get(e.id);
        return {
          employeeId: String(e.id),
          fullName: e.fullName ?? 'Employee',
          jobTitle: e.designation ?? '',
          department: e.department,
          todayStatus: a ? (a.missingPunch ? 'MISSING_PUNCH' : mapStatus(a.status)) : 'ABSENT',
          clockIn: a?.firstInUtc ?? undefined,
          clockOut: a?.lastOutUtc ?? undefined,
        };
      });
  },
};
