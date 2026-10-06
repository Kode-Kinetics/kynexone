import client from './client';

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
