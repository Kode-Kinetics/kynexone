import client from './client';

// ── Types ──────────────────────────────────────────────────────────────────────

export interface ReportCatalogItem {
  key: string;
  name: string;
  category: string;
  description: string;
}

export interface ReportFilters {
  dateFrom?: string;
  dateTo?: string;
  department?: string;
  location?: string;
  status?: string;
  period?: string;
  daysAhead?: number;
  /** W2-B — expense category code (finance.expense-claims). */
  category?: string;
}

export interface ReportResult {
  reportKey: string;
  generatedAt: string;
  rowCount: number;
  durationMs: number;
  data: unknown[];
}

export interface SavedReport {
  id: string;
  reportKey: string;
  name: string;
  category: string;
  filtersJson: string;
  columnsJson: string;
  isShared: boolean;
  createdByName: string;
  createdAtUtc: string;
}

export interface ReportSchedule {
  id: string;
  reportKey: string;
  reportName: string;
  category: string;
  frequency: string;
  deliveryMethod: string;
  recipients: string;
  exportFormat: string;
  isActive: boolean;
  lastRunAtUtc?: string;
  nextRunAtUtc?: string;
  createdAtUtc: string;
}

export interface ReportExecutionLog {
  id: string;
  reportKey: string;
  reportName: string;
  exportFormat: string;
  status: string;
  rowCount: number;
  errorMessage?: string;
  fileUrl?: string;
  runByName: string;
  createdAtUtc: string;
  durationMs: number;
}

export interface AnalyticsKPIs {
  headcount: { totalActive: number; newThisMonth: number; exitsThisMonth: number };
  leave: { pendingLeave: number; onLeaveToday: number };
  attendance: { presentToday: number; lateToday: number };
  overtime: { pendingOT: number };
  payroll: { lastRunYear?: number; lastRunMonth?: number; lastRunStatus?: string; totalNetSalary?: number };
  compliance: { visasExpiring: number; passportsExpiring: number };
  recruitment: { openPositions: number; pendingApplications: number };
  financial: { activeLoans: number; outstandingLoanBalance: number };
  generatedAt: string;
}

// ── API clients ───────────────────────────────────────────────────────────────

export const reportsApi = {
  catalog: () =>
    client.get<ReportCatalogItem[]>('/api/reports/catalog').then(r => r.data),

  run: (reportKey: string, filters?: ReportFilters) =>
    client.post<ReportResult>('/api/reports/run', { reportKey, filters }).then(r => r.data),

  listSaved: () =>
    client.get<SavedReport[]>('/api/reports/saved').then(r => r.data),

  save: (body: { reportKey: string; name: string; category: string; filters?: ReportFilters; columns?: string[]; isShared: boolean }) =>
    client.post<SavedReport>('/api/reports/saved', body).then(r => r.data),

  deleteSaved: (id: string) =>
    client.delete(`/api/reports/saved/${id}`),

  listSchedules: () =>
    client.get<ReportSchedule[]>('/api/reports/schedules').then(r => r.data),

  createSchedule: (body: { reportKey: string; reportName: string; category: string; filters?: ReportFilters; frequency: string; deliveryMethod: string; recipients?: string; exportFormat: string }) =>
    client.post<ReportSchedule>('/api/reports/schedules', body).then(r => r.data),

  toggleSchedule: (id: string) =>
    client.patch<ReportSchedule>(`/api/reports/schedules/${id}/toggle`).then(r => r.data),

  deleteSchedule: (id: string) =>
    client.delete(`/api/reports/schedules/${id}`),

  executions: (params: { reportKey?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; items: ReportExecutionLog[] }>('/api/reports/executions', { params }).then(r => r.data),
};

export const analyticsApi = {
  kpis: () =>
    client.get<AnalyticsKPIs>('/api/analytics/kpis').then(r => r.data),

  headcountTrend: (months = 6) =>
    client.get<{ period: string; headcount: number }[]>('/api/analytics/trends/headcount', { params: { months } }).then(r => r.data),

  payrollTrend: (months = 6) =>
    client.get('/api/analytics/trends/payroll', { params: { months } }).then(r => r.data),

  attendanceTrend: (days = 30) =>
    client.get('/api/analytics/trends/attendance', { params: { days } }).then(r => r.data),

  leaveTrend: (months = 6) =>
    client.get('/api/analytics/trends/leave', { params: { months } }).then(r => r.data),

  overtimeTrend: (months = 6) =>
    client.get('/api/analytics/trends/overtime', { params: { months } }).then(r => r.data),

  departmentComparison: () =>
    client.get('/api/analytics/department-comparison').then(r => r.data),
};
