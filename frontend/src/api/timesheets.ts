import client from './client';
import type { PagedResult } from './organization';

// Weekly timesheets: the employee enters hours against a day and a cost centre, submits the
// week, and a manager decides it through the product's one approval engine. Approved hours are
// reconciled against attendance, and that reconciliation is what the variance report serves.

export interface TimesheetEntry {
  id: string;
  workDate: string;
  costCenterId: string | null;
  costCenterCode: string | null;
  costCenterName: string | null;
  minutes: number;
  notes: string;
}

/** One day of the period: what was logged against what attendance recorded. */
export interface TimesheetDay {
  date: string;
  loggedMinutes: number;
  attendanceMinutes: number | null;
  /** AttendanceDailyRecord.Status, or 'NoRecord'. */
  attendanceStatus: string;
  varianceMinutes: number | null;
  isOverAllocated: boolean;
}

export interface TimesheetApproval {
  approvalRequestId: string;
  status: string;
  currentStepOrder: number;
  currentApproverName: string;
  currentApproverRole: string;
  dueAtUtc: string | null;
}

export interface Timesheet {
  id: string;
  employeeId: number;
  employeeName: string;
  companyId: string | null;
  periodStart: string;
  periodEnd: string;
  status: string;
  totalMinutes: number;
  isEditable: boolean;
  submittedAtUtc: string | null;
  decidedAtUtc: string | null;
  decisionComments: string | null;
  approvalRequestId: string | null;
  version: number;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  entries: TimesheetEntry[];
  days: TimesheetDay[];
  approval: TimesheetApproval | null;
}

export interface TimesheetSummary {
  id: string;
  employeeId: number;
  employeeName: string;
  companyId: string | null;
  periodStart: string;
  periodEnd: string;
  status: string;
  totalMinutes: number;
  submittedAtUtc: string | null;
  decidedAtUtc: string | null;
  approvalRequestId: string | null;
}

export interface TimesheetInboxItem {
  approvalRequestId: string;
  title: string;
  createdAtUtc: string;
  dueAtUtc: string | null;
  timesheet: Timesheet;
}

export interface TimesheetVarianceRow {
  timesheetId: string;
  employeeId: number;
  employeeName: string;
  workDate: string;
  loggedMinutes: number;
  attendanceMinutes: number | null;
  varianceMinutes: number | null;
  attendanceStatus: string;
  isOverAllocated: boolean;
}

export interface TimesheetVarianceReport {
  from: string;
  to: string;
  toleranceMinutes: number;
  loggedMinutes: number;
  attendanceMinutes: number;
  daysWithoutAttendance: number;
  overAllocatedDays: number;
  items: TimesheetVarianceRow[];
}

export interface SaveTimesheetEntry {
  workDate: string;
  costCenterId?: string | null;
  minutes: number;
  notes?: string;
}

/** The employee's own week. Every route is pinned server-side to the caller's employee record. */
export const essTimesheetsApi = {
  current: (date?: string) =>
    client.get<Timesheet>('/api/ess/timesheets/current', { params: date ? { date } : undefined }).then((r) => r.data),

  mine: (count = 12) =>
    client.get<TimesheetSummary[]>('/api/ess/timesheets', { params: { count } }).then((r) => r.data),

  saveEntries: (id: string, entries: SaveTimesheetEntry[], version?: number) =>
    client.put<Timesheet>(`/api/ess/timesheets/${id}/entries`, { entries, version }).then((r) => r.data),

  submit: (id: string) =>
    client.post<Timesheet>(`/api/ess/timesheets/${id}/submit`).then((r) => r.data),
};

/** The manager / HR side: the register, the approval queue, the decision, the variance report. */
export const timesheetsApi = {
  list: (
    params: {
      status?: string;
      employeeId?: number;
      from?: string;
      to?: string;
      search?: string;
      page?: number;
      pageSize?: number;
    } = {},
  ) => client.get<PagedResult<TimesheetSummary>>('/api/timesheets', { params }).then((r) => r.data),

  get: (id: string) => client.get<Timesheet>(`/api/timesheets/${id}`).then((r) => r.data),

  inbox: (queue: 'mine' | 'team' | 'all' = 'mine', pageSize = 50) =>
    client
      .get<{ items: TimesheetInboxItem[]; total: number; page: number; pageSize: number }>(
        '/api/timesheets/inbox',
        { params: { queue, pageSize } },
      )
      .then((r) => r.data),

  decide: (id: string, decision: 'Approve' | 'Reject', comments?: string) =>
    client.post<Timesheet>(`/api/timesheets/${id}/decision`, { decision, comments }).then((r) => r.data),

  attendanceVariance: (params: { from: string; to: string; employeeId?: number; overAllocatedOnly?: boolean }) =>
    client
      .get<TimesheetVarianceReport>('/api/timesheets/reports/attendance-variance', { params })
      .then((r) => r.data),
};
