import client from './client';
import { fetchAllPages } from '../lib/paging';
import type { LeaveRequest, LeaveType } from './leave';
import type { OvertimeRequest, OvertimeType } from './overtime';

export interface EssDashboard {
  profile: {
    employeeId: number;
    employeeCode: string;
    fullName: string;
    jobTitle: string;
    department: string;
    profilePhotoUrl: string;
    profileCompletenessScore: number;
  };
  attendanceToday: null | {
    workDate: string;
    status: string;
    firstInUtc?: string;
    lastOutUtc?: string;
    totalWorkedMinutes: number;
    missingPunch: boolean;
    lateMinutes: number;
    overtimeMinutes: number;
  };
  leaveBalances: Array<{
    leaveTypeId: string;
    leaveTypeName: string;
    entitled: number;
    used: number;
    pending: number;
    available: number;
    /** Saudi statutory event leave: statutory days per event, shown instead of `available`. */
    statutoryEntitlementDays?: number | null;
  }>;
  pendingRequests: number;
  documentAlerts: EssDocument[];
  announcements: Array<{ id: string; title: string; body: string; audience: string; publishedAtUtc: string }>;
  notifications: EssNotification[];
  actionItems: Array<{ id: string; title: string; category: string; dueAtUtc?: string }>;
  payrollSnapshot: {
    netSalary: number;
    currency: string;
    period: string;
    nextPayrollDate: string | null;
  } | null;
  loansSummary: {
    totalOutstanding: number;
    currency: string;
    activeLoanCount: number;
    nextInstallmentAmount: number | null;
    nextInstallmentDate: string | null;
  } | null;
  loanSummaries?: Array<{ totalOutstanding: number; currency: string; activeLoanCount: number; nextInstallmentAmount: number | null; nextInstallmentDate: string | null }>;
  performanceSnapshot: {
    cycleName: string;
    goalsCompleted: number;
    goalsTotal: number;
    lastRating: number | null;
  } | null;
  overtimeHoursThisMonth: number;
  nextApprovedLeave: {
    leaveTypeName: string;
    startDate: string;
    endDate: string;
    days: number;
  } | null;
  tenureMonths: number;
}

export interface EssDocument {
  id: string;
  documentType: string;
  fileName: string;
  expiryDate?: string;
  approvalStatus: string;
}

export interface EssNotification {
  id: string;
  title: string;
  body: string;
  notificationType: string;
  isRead: boolean;
  createdAtUtc: string;
}

export interface HrRequestPayload {
  categoryId?: string;
  categoryName?: string;
  subject: string;
  description: string;
  priority?: string;
}

export interface EssHrRequest {
  id: string;
  subject: string;
  description: string;
  priority: string;
  status: string;
  categoryName: string;
  dueAtUtc: string;
  createdAtUtc: string;
  hrResponded: boolean;
  isOverdue: boolean;
  responseStatus: string;
}

export interface EssHrRequestComment {
  id: string;
  comment: string;
  authorType: string;
  authorName: string;
  createdAtUtc: string;
}

export interface EssHrRequestDetail {
  request: EssHrRequest;
  comments: EssHrRequestComment[];
  hrResponded: boolean;
  isOverdue: boolean;
  responseStatus: string;
}

export const essApi = {
  dashboard: () => client.get<EssDashboard>('/api/ess/dashboard').then((r) => r.data),
  profile: () => client.get('/api/ess/profile').then((r) => r.data),
  attendance: () => client.get('/api/ess/attendance').then((r) => r.data),
  leaveBalance: () => client.get('/api/ess/leave/balance').then((r) => r.data),
  documents: () => client.get<EssDocument[]>('/api/ess/documents').then((r) => r.data),
  hrRequests: () => client.get<EssHrRequest[]>('/api/ess/hr-requests/my').then((r) => r.data),
  hrRequestDetail: (id: string) => client.get<EssHrRequestDetail>(`/api/ess/hr-requests/${id}`).then((r) => r.data),
  addHrRequestComment: (id: string, comment: string) => client.post<EssHrRequestComment>(`/api/ess/hr-requests/${id}/comments`, { comment }).then((r) => r.data),
  createHrRequest: (payload: HrRequestPayload) => client.post('/api/ess/hr-requests', payload).then((r) => r.data),
  askAi: (question: string) => client.post<{ answer: string }>('/api/ess/ai/ask', { question }).then((r) => r.data),
  notifications: () => client.get<EssNotification[]>('/api/ess/notifications').then((r) => r.data),
  markNotificationRead: (id: string) => client.patch(`/api/ess/notifications/${id}/read`),
  myRoster: (from: string, to: string) =>
    client.get<EssRosterEntry[]>('/api/ess/my-roster', { params: { from, to } }).then((r) => r.data),
  /** The caller's own finalised payslips, newest period first. */
  payslips: () => client.get<EssPayslipSummary[]>('/api/ess/payslips').then((r) => r.data),
  /** One of the caller's own payslips, with its lines. A colleague's id answers 404. */
  payslipDetail: (id: string) => client.get<EssPayslipDetail>(`/api/ess/payslips/${id}`).then((r) => r.data),
  /** Streams the caller's own payslip PDF and saves it. */
  downloadPayslip: (id: string, filename: string) =>
    client.get(`/api/ess/payslips/${id}/download`, { responseType: 'blob' }).then((r) => {
      const url = URL.createObjectURL(new Blob([r.data], { type: 'application/pdf' }));
      const a = document.createElement('a');
      a.href = url;
      a.download = filename;
      a.click();
      URL.revokeObjectURL(url);
    }),
};

export interface EssPayslipSummary {
  id: string;
  runId: string;
  year: number;
  month: number;
  periodLabel: string;
  currency: string;
  runType: string;
  grossSalary: number;
  totalDeductions: number;
  netSalary: number;
}

/**
 * Line types, as the backend's PayslipLineTypes. An EmployerContribution line (e.g. the employer's
 * GOSI occupational hazard) is an employer cost: it is never part of totalDeductions.
 */
export type EssPayslipLineType = 'Earning' | 'Deduction' | 'EmployerContribution' | 'Net';

export interface EssPayslipLine {
  name: string;
  amount: number;
  type: EssPayslipLineType | string;
  /** The pay component's Arabic name, when the catalogue has one. */
  nameAr?: string | null;
}

export interface EssPayslipDetail {
  id: string;
  year: number;
  month: number;
  periodLabel: string;
  currency: string;
  runType: string;
  grossSalary: number;
  totalDeductions: number;
  netSalary: number;
  /** True when gross − deductions = net. */
  reconciled: boolean;
  lines: EssPayslipLine[];
  ytdGross: number;
  ytdNet: number;
  /** Paid by the employer on top of the salary. Not part of totalDeductions. */
  employerContributions?: number;
}

export interface EssRosterEntry {
  id: string;
  date: string;
  shiftDefinitionId: string;
  shiftName: string;
  shiftCode: string;
  shiftColor: string;
}

// ── Self-service actions ──────────────────────────────────────────────────────
// Everything an employee does from /ess/leave, /ess/overtime and /ess/requests, through the endpoints
// the mobile app already uses. None needs an HR permission and each is limited to the caller's own
// record on the server: the /api/ess/* endpoints by the ESS context (the caller's own employee),
// /api/leave/requests and /api/overtime/requests by the caller's data scope (an Employee's scope is
// their own record; cancel refuses anyone else's request). The list calls still pass the caller's own
// id, so a manager or HR user who opens their own self-service page sees their requests, not the
// team's or the whole company's that their wider scope would otherwise return.

export interface EssBalance {
  leaveTypeId: string;
  leaveTypeName: string;
  entitled: number;
  used: number;
  pending: number;
  available: number;
  /** Saudi statutory event leave: statutory days per event, shown instead of `available`. */
  statutoryEntitlementDays?: number | null;
}

export interface EssLeaveApplication {
  leaveTypeId: string;
  startDate: string;
  endDate: string;
  dayType?: 'Full' | 'Half';
  reason: string;
  statutoryEventDate?: string;
  separateEventReason?: string;
}

export interface EssOvertimeApplication {
  workDate: string;
  startTimeUtc: string;
  endTimeUtc: string;
  reason: string;
  overtimeTypeId?: string;
}

export interface EssHrRequestCategory {
  id: string;
  name: string;
  code: string;
  defaultSlaHours: number;
  isActive: boolean;
}

export const essActionsApi = {
  /** The caller's own employee id, as the ESS context resolves it (used when the login carries none). */
  ownEmployeeId: () => client.get<{ id: number }>('/api/ess/profile').then((r) => r.data.id),

  leaveBalances: () => client.get<EssBalance[]>('/api/ess/leave/balance').then((r) => r.data),
  leaveTypes: () => client.get<LeaveType[]>('/api/leave/types').then((r) => r.data.filter((x) => x.isActive)),
  /** The caller's own leave requests, every page. */
  myLeaveRequests: (employeeId: number) =>
    fetchAllPages((page, pageSize) =>
      client.get<{ items: LeaveRequest[]; total: number; page: number }>('/api/leave/requests', { params: { employeeId, page, pageSize } }).then((r) => r.data)),
  /** Submitted through the ESS endpoint: same balance, overlap and approval routing as HR-side requests. */
  applyLeave: (body: EssLeaveApplication) => client.post<LeaveRequest>('/api/ess/leave/request', body).then((r) => r.data),
  /** Cancels one of the caller's own requests (the server refuses anyone else's). */
  cancelLeave: (id: string, reason: string) => client.post<LeaveRequest>(`/api/leave/requests/${id}/cancel`, { reason }).then((r) => r.data),

  overtimeTypes: () => client.get<OvertimeType[]>('/api/overtime/types').then((r) => r.data.filter((x) => x.isActive)),
  /** The caller's own overtime requests, every page. */
  myOvertime: (employeeId: number) =>
    fetchAllPages((page, pageSize) =>
      client.get<{ items: OvertimeRequest[]; total: number; page: number }>('/api/overtime/requests', { params: { employeeId, page, pageSize } }).then((r) => r.data)),
  /** The server refuses an employee id outside the caller's scope; for an Employee that is anyone but themselves. */
  requestOvertime: (employeeId: number, body: EssOvertimeApplication) =>
    client.post<OvertimeRequest>('/api/overtime/requests', { ...body, employeeId, source: 'SelfService' }).then((r) => r.data),

  hrRequestCategories: () => client.get<EssHrRequestCategory[]>('/api/hr-requests/categories').then((r) => r.data.filter((x) => x.isActive)),
};
