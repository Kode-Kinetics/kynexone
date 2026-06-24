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
};
