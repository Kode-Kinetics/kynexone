import client from './client';

export interface DashboardSummary {
  totalEmployees: number;
  activeEmployees: number;
  presentToday: number;
  onLeave: number;
  absent: number;
  overtimeHours: number;
  churnRisk: number;
  /** Attendance rows for the tenant-local date, any status. 0 means nothing captured yet —
   *  distinct from "everyone absent". Absent on older API versions. */
  attendanceRecordsToday?: number;
  lastPunchAtUtc?: string | null;
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
  /** Who the request is about. Absent on older API versions — the UI falls back to parsing
   *  the "EMP-… Name" tail of `title`. */
  employeeId?: number | null;
  employeeCode?: string | null;
  employeeName?: string | null;
  /** When the approval is due (ApprovalRequest.DueAtUtc), if a due date is set. */
  dueAtUtc?: string | null;
  department?: string | null;
  /** Short human detail: leave dates, changed fields. */
  detail?: string | null;
}

export interface DashboardPayrollSummary {
  periodLabel: string;
  totalGross: number;
  totalNet: number;
  totalDeductions: number;
  employeeCount: number;
  status: string;
  payDate?: string | null;
  employerContributions?: number | null;
}

export interface NamedValue {
  name: string;
  value: number;
}

export interface DashboardAlert {
  title: string;
  severity: 'Info' | 'Warning' | 'Critical';
  employeeId?: number | null;
  employeeName?: string | null;
  expiryDate?: string | null;
  /** Negative when already expired. */
  daysRemaining?: number | null;
  kind?: string | null;
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
  /** Uncapped counts over the same filtered set as `alerts` (which is capped). */
  complianceAlertsTotal?: number;
  complianceCriticalTotal?: number;
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

export interface HeatmapCell { date: string; rostered: number; attended: number; rate: number | null }
export interface HeatmapDepartment { name: string; headcount: number; cells: HeatmapCell[] }

export interface DashboardAnalytics {
  attendanceHeatmap?: { days: string[]; departments: HeatmapDepartment[] } | null;
  leaveUsage?: { year: number; takenDays: number; entitlementDays: number | null; byType: Array<{ type: string; days: number }> } | null;
  /** Null when the Saudization module is off. */
  nationality?: { saudi: number; nonSaudi: number; unknown: number; saudizationPct: number | null; nitaqatBand: string | null } | null;
  headcountTrend?: Array<{ month: string; active: number }>;
}

export interface DashboardFull {
  summary: DashboardSummary;
  trends: DashboardTrend[];
  overview: DashboardOverview;
  payrollTrends: PayrollTrend[];
  activityFeed: ActivityFeedItem[];
  kpis: DashboardKpis;
  /** Absent on older API versions; every consumer falls back. */
  analytics?: DashboardAnalytics | null;
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
