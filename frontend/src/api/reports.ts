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
  // F3: a schedule whose creator was deactivated used to fail every period in silence.
  consecutiveFailureCount: number;
  lastFailureAtUtc?: string;
  lastFailureReason: string;
  ownerInvalidatedAtUtc?: string;
}

/** The formats the API will actually produce. PDF is deliberately absent — see ReportExportFormats. */
export type ReportExportFormat = 'csv' | 'xlsx';

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

/**
 * Figures the caller lacks the data permission for come back null (the server withholds them rather
 * than sending zero), so every count is nullable and must render as restricted, not as 0.
 */
export interface AnalyticsKPIs {
  headcount: { totalActive: number | null; newThisMonth: number | null; exitsThisMonth: number | null };
  leave: { pendingLeave: number | null; onLeaveToday: number | null };
  attendance: { presentToday: number | null; lateToday: number | null };
  overtime: { pendingOT: number | null };
  payroll: { lastRunYear?: number | null; lastRunMonth?: number | null; lastRunStatus?: string | null; totalNetSalary?: number | null };
  compliance: { visasExpiring: number | null; passportsExpiring: number | null };
  recruitment: { openPositions: number | null; pendingApplications: number | null };
  financial: { activeLoans: number | null; outstandingLoanBalance: number | null };
  generatedAt: string;
}

// ── API clients ───────────────────────────────────────────────────────────────

export const reportsApi = {
  catalog: () =>
    client.get<ReportCatalogItem[]>('/api/reports/catalog').then(r => r.data),

  run: (reportKey: string, filters?: ReportFilters) =>
    client.post<ReportResult>('/api/reports/run', { reportKey, filters }).then(r => r.data),

  /**
   * Downloads the WHOLE result, not the 200 rows the table shows. Returns the filename the
   * server chose so the caller can report what landed.
   */
  export: async (reportKey: string, format: ReportExportFormat, filters?: ReportFilters) => {
    const response = await client.post(
      '/api/reports/export',
      { reportKey, filters, format },
      { responseType: 'blob' },
    );
    const disposition = String(response.headers['content-disposition'] ?? '');
    const match = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(disposition);
    const filename = match ? decodeURIComponent(match[1]) : `${reportKey}.${format}`;
    const url = URL.createObjectURL(new Blob([response.data], { type: String(response.headers['content-type'] ?? '') }));
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = filename;
    anchor.click();
    URL.revokeObjectURL(url);
    return { filename, rowCount: Number(response.headers['x-report-row-count'] ?? 0) };
  },

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
