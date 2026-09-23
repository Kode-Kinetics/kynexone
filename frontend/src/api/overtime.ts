import client from './client';
import type { PagedResult } from './organization';

export interface OvertimePolicy {
  id: string;
  code: string;
  name: string;
  hourlyRateBasis: string;
  fixedHourlyRate: number;
  standardMonthlyHours: number;
  minimumMinutes: number;
  maximumMinutesPerDay: number;
  monthlyCapMinutes: number;
  roundingRule: string;
  requiresApproval: boolean;
  allowCompOffConversion: boolean;
  isActive: boolean;
  createdAtUtc: string;
}

export interface OvertimeType {
  id: string;
  code: string;
  name: string;
  category: string;
  isActive: boolean;
}

export interface OvertimeMultiplier {
  id: string;
  overtimePolicyId: string;
  dayCategory: string;
  multiplier: number;
  isActive: boolean;
}

export interface OvertimeRequest {
  id: string;
  employeeId: number;
  employeeName: string;
  overtimePolicyId: string | null;
  overtimeTypeId: string | null;
  workDate: string;
  startTimeUtc: string;
  endTimeUtc: string;
  requestedMinutes: number;
  approvedMinutes: number;
  source: string;
  reason: string;
  status: string;
  createdAtUtc: string;
  decidedAtUtc: string | null;
}

/** An attendance day the overtime policy would not pay in full. */
export interface OvertimeCapException {
  id: string;
  employeeId: number;
  dailyRecordId: string | null;
  workDate: string;
  exceptionType: string;
  severity: string;
  details: string;
  isResolved: boolean;
  createdAtUtc: string;
}

/**
 * What a detection run did. `capped` is the overtime the tenant's configured policy will not pay:
 * attendance-derived overtime used to bypass the rounding rule, the minimum, the daily maximum and
 * the monthly cap entirely, so the same hours were paid in full by one door and capped by the other.
 * Capping without showing the excess would only make that quieter, so it comes back here.
 */
export interface DetectOvertimeResult {
  created: OvertimeRequest[];
  capped: OvertimeCapException[];
}

export interface OvertimeCalculation {
  id: string;
  overtimeRequestId: string;
  employeeId: number;
  approvedHours: number;
  hourlyRate: number;
  multiplier: number;
  amount: number;
  currency: string;
  calculationJson: string;
  createdAtUtc: string;
}

export interface OvertimePayrollImpact {
  id: string;
  overtimeRequestId: string;
  employeeId: number;
  payrollRunId: string | null;
  hours: number;
  amount: number;
  status: string;
  createdAtUtc: string;
  processedAtUtc: string | null;
}

export interface OvertimeBudget {
  id: string;
  departmentId: string | null;
  year: number;
  month: number;
  budgetAmount: number;
  consumedAmount: number;
  currency: string;
}

export interface OvertimeCompOffConversion {
  id: string;
  overtimeRequestId: string;
  employeeId: number;
  overtimeHours: number;
  compOffDays: number;
  status: string;
  createdAtUtc: string;
}

export interface OvertimeSummary {
  totalRequests: number;
  approvedRequests: number;
  pendingRequests: number;
  approvedHours: number;
  payrollAmount: number;
}

export const overtimeApi = {
  policies: () =>
    client.get<OvertimePolicy[]>('/api/overtime/policies').then((r) => r.data),

  createPolicy: (payload: Partial<OvertimePolicy> & { regularDayMultiplier?: number; weekendMultiplier?: number; holidayMultiplier?: number }) =>
    client.post<OvertimePolicy>('/api/overtime/policies', payload).then((r) => r.data),

  types: () =>
    client.get<OvertimeType[]>('/api/overtime/types').then((r) => r.data),

  createType: (code: string, name: string, category = 'Regular') =>
    client.post<OvertimeType>('/api/overtime/types', { code, name, category }).then((r) => r.data),

  requests: (params: { status?: string; employeeId?: number; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<OvertimeRequest>>('/api/overtime/requests', { params }).then((r) => r.data),

  createRequest: (payload: {
    employeeId: number;
    overtimePolicyId?: string;
    overtimeTypeId?: string;
    workDate: string;
    startTimeUtc: string;
    endTimeUtc: string;
    source?: string;
    reason?: string;
  }) => client.post<OvertimeRequest>('/api/overtime/requests', payload).then((r) => r.data),

  approve: (id: string, approvedMinutes: number, notes?: string) =>
    client.post<OvertimeCalculation>(`/api/overtime/requests/${id}/approve`, { approvedMinutes, notes }).then((r) => r.data),

  reject: (id: string, notes?: string) =>
    client.post<OvertimeRequest>(`/api/overtime/requests/${id}/reject`, { approvedMinutes: 0, notes }).then((r) => r.data),

  detectFromAttendance: (fromDate: string, toDate: string, overtimePolicyId?: string) =>
    client.post<DetectOvertimeResult>('/api/overtime/detect-from-attendance', { fromDate, toDate, overtimePolicyId }).then((r) => r.data),

  calculations: (employeeId?: number) =>
    client.get<OvertimeCalculation[]>('/api/overtime/calculations', { params: employeeId ? { employeeId } : undefined }).then((r) => r.data),

  payrollReview: () =>
    client.get<OvertimePayrollImpact[]>('/api/overtime/payroll-review').then((r) => r.data),

  budgets: (year?: number) =>
    client.get<OvertimeBudget[]>('/api/overtime/budgets', { params: year ? { year } : undefined }).then((r) => r.data),

  compOffConversions: (employeeId?: number) =>
    client.get<OvertimeCompOffConversion[]>('/api/overtime/comp-off-conversions', { params: employeeId ? { employeeId } : undefined }).then((r) => r.data),

  createCompOffConversion: (overtimeRequestId: string, compOffDays: number) =>
    client.post<OvertimeCompOffConversion>('/api/overtime/comp-off-conversions', { overtimeRequestId, compOffDays }).then((r) => r.data),

  summary: (from?: string, to?: string) =>
    client.get<OvertimeSummary>('/api/overtime/reports/summary', { params: { from, to } }).then((r) => r.data),
};
