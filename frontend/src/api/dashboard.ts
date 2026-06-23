import client from './client';

export interface DashboardSummary {
  totalEmployees: number;
  activeEmployees: number;
  presentToday: number;
  onLeave: number;
  absent: number;
  overtimeHours: number;
  churnRisk: number;
}

export interface DashboardTrend {
  month: string;
  attendanceRate: number;
  overtimeHours: number;
}

export interface PayrollTrend {
  month: string;
  totalNet: number;
  employeeCount: number;
  status: string;
}

export interface ActivityFeedItem {
  module: string;
  action: string;
  actor: string;
  occurredAt: string;
}

export interface ApprovalQueueItem {
  id: string;
  title: string;
  module: string;
  createdAtUtc: string;
}

export interface DashboardPayrollSummary {
  periodLabel: string;
  totalGross: number;
  totalNet: number;
  totalDeductions: number;
  employeeCount: number;
  status: string;
}

export interface NamedValue {
  name: string;
  value: number;
}

export interface DashboardAlert {
  title: string;
  severity: 'Info' | 'Warning' | 'Critical';
}

export interface DashboardOverview {
  pendingApprovals: number;
  approvalQueue: ApprovalQueueItem[];
  payrollSummary: DashboardPayrollSummary | null;
  payrollByEntity: NamedValue[];
  workforceMix: NamedValue[];
  headcountByDepartment: NamedValue[];
  alerts: DashboardAlert[];
  openLeaveRequests: number;
  newJoinersThisMonth: number;
}

export interface DashboardKpis {
  pendingLeaveRequests: number;
  pendingAttendanceCorrections: number;
  attendanceExceptions: number;
  expiringDocuments: number;
  expiredDocuments: number;
  missingDocuments: number;
  qiwaEnabled: boolean;
}

export interface DashboardFull {
  summary: DashboardSummary;
  trends: DashboardTrend[];
  overview: DashboardOverview;
  payrollTrends: PayrollTrend[];
  activityFeed: ActivityFeedItem[];
  kpis: DashboardKpis;
}

export const dashboardApi = {
  full: (months = 6) =>
    client.get<DashboardFull>('/api/dashboard/full', { params: { months } }).then((r) => r.data),
  // kept for backwards compatibility
  kpis: () => client.get<DashboardKpis>('/api/dashboard/kpis').then((r) => r.data),
  summary: () => client.get<DashboardSummary>('/api/dashboard/summary').then((r) => r.data),
  trends: (months = 6) =>
    client.get<DashboardTrend[]>('/api/dashboard/trends', { params: { months } }).then((r) => r.data),
  overview: () => client.get<DashboardOverview>('/api/dashboard/overview').then((r) => r.data),
};
