import client from './client';
import type { PagedResult } from './organization';

// ── Types ──────────────────────────────────────────────────────────────────────

export interface LeaveType {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  category: string;
  isPaid: boolean;
  isHalfDayAllowed: boolean;
  isHourlyAllowed: boolean;
  requiresAttachment: boolean;
  requiresReason: boolean;
  maxConsecutiveDays: number;
  colorCode: string;
  isActive: boolean;
  sortOrder: number;
  createdAtUtc: string;
}

export interface LeavePolicy {
  id: string;
  name: string;
  leaveTypeId: string;
  countryCode: string;
  companyId: string | null;
  branchId: string | null;
  departmentName: string;
  grade: string;
  employmentType: string;
  contractType: string;
  gender: string;
  appliesOnProbation: boolean;
  annualEntitlementDays: number;
  accrualMethod: string;
  carryForwardMax: number;
  carryForwardExpiry: number;
  encashmentAllowed: boolean;
  encashmentMaxDays: number;
  minimumDaysPerRequest: number;
  maximumDaysPerRequest: number;
  noticeRequiredDays: number;
  weekendsIncluded: boolean;
  publicHolidaysIncluded: boolean;
  payrollImpact: string;
  approvalWorkflowId: string | null;
  status: string;
  createdAtUtc: string;
  updatedAtUtc: string | null;
}

export interface EmployeeLeaveBalance {
  id: string;
  employeeId: number;
  employeeName: string;
  leaveTypeId: string;
  leaveTypeName: string;
  year: number;
  entitled: number;
  accrued: number;
  used: number;
  pending: number;
  carriedForward: number;
  encashed: number;
  expired: number;
  manualAdjustment: number;
  negativeAllowed: boolean;
  available: number;
  createdAtUtc: string;
  updatedAtUtc: string | null;
}

export interface LeaveBalanceTransaction {
  id: string;
  employeeId: number;
  leaveTypeId: string;
  year: number;
  transactionType: string;
  amount: number;
  balanceBefore: number;
  balanceAfter: number;
  reference: string;
  reason: string;
  performedByName: string;
  createdAtUtc: string;
}

export interface LeaveRequest {
  id: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  designationTitle: string;
  leaveTypeId: string;
  leaveTypeName: string;
  policyId: string | null;
  startDate: string;
  endDate: string;
  totalDays: number;
  dayType: string;
  hoursRequested: number;
  reason: string;
  isEmergency: boolean;
  attachmentPath: string;
  payrollImpact: string;
  status: string;
  managerApprovalNotes: string;
  hrApprovalNotes: string;
  rejectionReason: string;
  cancellationReason: string;
  returnDate: string | null;
  delegateEmployeeId: number | null;
  delegateEmployeeName: string;
  createdAtUtc: string;
  submittedAtUtc: string | null;
  decidedAtUtc: string | null;
  cancelledAtUtc: string | null;
  approvals?: LeaveApproval[];
}

export interface LeaveApproval {
  id: string;
  leaveRequestId: string;
  stepNumber: number;
  approverRole: string;
  approverId: string | null;
  approverName: string;
  decision: string;
  notes: string;
  actedAtUtc: string | null;
  createdAtUtc: string;
}

export interface PublicHolidayCalendar {
  id: string;
  name: string;
  countryCode: string;
  companyId: string | null;
  branchId: string | null;
  calendarYear: number;
  isActive: boolean;
  createdAtUtc: string;
}

export interface PublicHoliday {
  id: string;
  calendarId: string;
  nameEn: string;
  nameAr: string;
  date: string;
  hijriDate: string;
  isRecurring: boolean;
  isOptional: boolean;
  holidayType: string;
  notes: string;
  createdAtUtc: string;
}

export interface LeaveBlackoutDate {
  id: string;
  nameEn: string;
  startDate: string;
  endDate: string;
  departmentName: string;
  reason: string;
  isCompanyWide: boolean;
  createdAtUtc: string;
}

export interface LeaveEncashmentRequest {
  id: string;
  employeeId: number;
  employeeName: string;
  companyId: string | null;
  leaveTypeId: string;
  leaveTypeName: string;
  year: number;
  daysToEncash: number;
  amountPerDay: number;
  totalAmount: number;
  currency: string;
  reason: string;
  status: string;
  hrNotes: string;
  payrollNotes: string;
  payrollRunId: string | null;
  payrollAdjustmentId: string | null;
  createdAtUtc: string;
  processedAtUtc: string | null;
  voidedAtUtc: string | null;
  voidReason: string;
}

export interface CompOffCredit {
  id: string;
  employeeId: number;
  employeeName: string;
  workedDate: string;
  workType: string;
  hoursWorked: number;
  daysEarned: number;
  expiryDate: string | null;
  status: string;
  managerApprovalNotes: string;
  approvedByName: string;
  createdAtUtc: string;
  approvedAtUtc: string | null;
}

export interface AbsenceRecord {
  id: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  absenceDate: string;
  absenceType: string;
  isRegularized: boolean;
  payrollImpact: string;
  regularizationRequestId: string | null;
  createdAtUtc: string;
}

export interface AbsenceRegularizationRequest {
  id: string;
  employeeId: number;
  employeeName: string;
  absenceRecordId: string;
  reason: string;
  leaveTypeId: string | null;
  status: string;
  managerNotes: string;
  hrNotes: string;
  createdAtUtc: string;
  reviewedAtUtc: string | null;
}

export interface LeaveDelegation {
  id: string;
  employeeId: number;
  employeeName: string;
  delegateEmployeeId: number;
  delegateEmployeeName: string;
  leaveRequestId: string | null;
  startDate: string;
  endDate: string;
  delegationType: string;
  notes: string;
  status: string;
  createdAtUtc: string;
}

export interface LeaveAIInsight {
  id: string;
  insightType: string;
  severity: string;
  title: string;
  summary: string;
  affectedEmployeeId: number | null;
  affectedDepartment: string;
  data: string;
  isAcknowledged: boolean;
  acknowledgedByName: string;
  createdAtUtc: string;
}

export interface LeaveCalendarEntry {
  employeeId: number;
  employeeName: string;
  departmentName: string;
  leaveTypeName: string;
  startDate: string;
  endDate: string;
  totalDays: number;
  status: string;
  colorCode: string;
}

export interface LeaveDashboard {
  onLeaveToday: number;
  pendingApprovals: number;
  pendingCancellations: number;
  pendingEncashments: number;
  unauthorizedAbsences: number;
  upcomingLeaves: number;
  activeDelegations: number;
  pendingCompOff: number;
}

// ── API ────────────────────────────────────────────────────────────────────────

export const leaveTypesApi = {
  list: () =>
    client.get<LeaveType[]>('/api/leave/types').then(r => r.data),
  create: (body: { code: string; nameEn: string; nameAr?: string; category: string; isPaid: boolean; isHalfDayAllowed: boolean; isHourlyAllowed: boolean; requiresAttachment: boolean; requiresReason: boolean; maxConsecutiveDays: number; colorCode?: string; sortOrder?: number }) =>
    client.post<LeaveType>('/api/leave/types', body).then(r => r.data),
  update: (id: string, body: object) =>
    client.put<LeaveType>(`/api/leave/types/${id}`, body).then(r => r.data),
  delete: (id: string) =>
    client.delete(`/api/leave/types/${id}`).then(r => r.data),
};

export const leavePoliciesApi = {
  list: (params: { countryCode?: string; status?: string; leaveTypeId?: string } = {}) =>
    client.get<PagedResult<LeavePolicy>>('/api/leave/policies', { params }).then(r => r.data.items ?? []),
  get: (id: string) =>
    client.get<LeavePolicy>(`/api/leave/policies/${id}`).then(r => r.data),
  create: (body: object) =>
    client.post<LeavePolicy>('/api/leave/policies', body).then(r => r.data),
  update: (id: string, body: object) =>
    client.put<LeavePolicy>(`/api/leave/policies/${id}`, body).then(r => r.data),
  delete: (id: string) =>
    client.delete(`/api/leave/policies/${id}`).then(r => r.data),
};

export const leaveBalancesApi = {
  list: (params: { employeeId?: number; leaveTypeId?: string; year?: number; companyId?: string; branchId?: string } = {}) =>
    client.get<PagedResult<EmployeeLeaveBalance>>('/api/leave/balances', { params }).then(r => r.data.items ?? []),
  forEmployee: (employeeId: number, year?: number) =>
    client.get<EmployeeLeaveBalance[]>(`/api/leave/balances/employee/${employeeId}`, { params: { year } }).then(r => r.data),
  adjust: (body: { employeeId: number; leaveTypeId: string; year: number; amount: number; reason: string }) =>
    client.post<EmployeeLeaveBalance>('/api/leave/balances/adjust', { employeeId: body.employeeId, leaveTypeId: body.leaveTypeId, amount: body.amount, reason: body.reason, year: body.year }).then(r => r.data),
  transactions: (employeeId: number, leaveTypeId?: string, year?: number) =>
    client.get<LeaveBalanceTransaction[]>('/api/leave/balances/transactions', { params: { employeeId, leaveTypeId, year } }).then(r => r.data),
  accrue: () =>
    client.post('/api/leave/balances/accrue').then(r => r.data),
};

export const leaveRequestsApi = {
  list: (params: { status?: string; employeeId?: number; leaveTypeId?: string; fromDate?: string; toDate?: string; departmentName?: string; companyId?: string; branchId?: string } = {}) =>
    client.get<{ items: LeaveRequest[]; total: number }>('/api/leave/requests', { params }).then(r => r.data),
  get: (id: string) =>
    client.get<LeaveRequest>(`/api/leave/requests/${id}`).then(r => r.data),
  create: (body: {
    employeeId: number; employeeName: string; departmentName?: string; designationTitle?: string;
    leaveTypeId: string; policyId?: string; startDate: string; endDate: string;
    dayType?: string; hoursRequested?: number; reason: string; isEmergency?: boolean;
    attachmentPath?: string; delegateEmployeeId?: number; delegateEmployeeName?: string;
  }) => client.post<LeaveRequest>('/api/leave/requests', body).then(r => r.data),
  approve: (id: string, notes?: string) =>
    client.post<LeaveRequest>(`/api/leave/requests/${id}/approve`, { notes }).then(r => r.data),
  reject: (id: string, reason: string) =>
    client.post<LeaveRequest>(`/api/leave/requests/${id}/reject`, { reason }).then(r => r.data),
  cancel: (id: string, reason: string) =>
    client.post<LeaveRequest>(`/api/leave/requests/${id}/cancel`, { reason }).then(r => r.data),
  withdraw: (id: string) =>
    client.post<LeaveRequest>(`/api/leave/requests/${id}/withdraw`).then(r => r.data),
  delegate: (id: string, delegateEmployeeId: number, delegateEmployeeName: string) =>
    client.post<LeaveRequest>(`/api/leave/requests/${id}/delegate`, { delegateEmployeeId, delegateEmployeeName }).then(r => r.data),
};

export const holidayCalendarApi = {
  listCalendars: (params: { countryCode?: string; year?: number } = {}) =>
    client.get<PublicHolidayCalendar[]>('/api/leave/holidays/calendars', { params }).then(r => r.data),
  createCalendar: (body: { name: string; countryCode: string; calendarYear: number; companyId?: string; branchId?: string }) =>
    client.post<PublicHolidayCalendar>('/api/leave/holidays/calendars', body).then(r => r.data),
  updateCalendar: (id: string, body: { name: string; countryCode: string; calendarYear: number }) =>
    client.put<PublicHolidayCalendar>(`/api/leave/holidays/calendars/${id}`, body).then(r => r.data),
  deleteCalendar: (id: string) =>
    client.delete(`/api/leave/holidays/calendars/${id}`).then(r => r.data),
  listHolidays: (calendarId: string) =>
    client.get<PublicHoliday[]>(`/api/leave/holidays/calendars/${calendarId}/holidays`).then(r => r.data),
  addHoliday: (calendarId: string, body: { nameEn: string; nameAr?: string; date: string; hijriDate?: string; isRecurring?: boolean; isOptional?: boolean; holidayType?: string; notes?: string }) =>
    client.post<PublicHoliday>(`/api/leave/holidays/calendars/${calendarId}/holidays`, body).then(r => r.data),
  updateHoliday: (id: string, body: { nameEn: string; nameAr?: string; date: string; hijriDate?: string; isRecurring?: boolean; isOptional?: boolean; holidayType?: string; notes?: string }) =>
    client.put<PublicHoliday>(`/api/leave/holidays/${id}`, body).then(r => r.data),
  deleteHoliday: (id: string) =>
    client.delete(`/api/leave/holidays/${id}`).then(r => r.data),
  inRange: (from: string, to: string) =>
    client.get<PublicHoliday[]>('/api/leave/holidays/range', { params: { from, to } }).then(r => r.data),
  today: () =>
    client.get<{ isHoliday: boolean; holiday: PublicHoliday | null }>('/api/leave/holidays/today').then(r => r.data),
  listBlackouts: () =>
    client.get<LeaveBlackoutDate[]>('/api/leave/holidays/blackouts').then(r => r.data),
  createBlackout: (body: { nameEn: string; startDate: string; endDate: string; departmentName?: string; reason: string; isCompanyWide?: boolean }) =>
    client.post<LeaveBlackoutDate>('/api/leave/holidays/blackouts', body).then(r => r.data),
};

export const encashmentApi = {
  list: (params: { status?: string; employeeId?: number; companyId?: string; branchId?: string } = {}) =>
    client.get<PagedResult<LeaveEncashmentRequest>>('/api/leave/encashment', { params }).then(r => r.data.items ?? []),
  create: (body: { employeeId: number; leaveTypeId: string; year: number; daysToEncash: number; reason: string }) =>
    client.post<LeaveEncashmentRequest>('/api/leave/encashment', body).then(r => r.data),
  hrApprove: (id: string, notes?: string) =>
    client.post<LeaveEncashmentRequest>(`/api/leave/encashment/${id}/hr-approve`, { notes }).then(r => r.data),
  payrollApprove: (id: string, payrollRunId: string, notes?: string) =>
    client.post<LeaveEncashmentRequest>(`/api/leave/encashment/${id}/payroll-approve`, { notes, payrollRunId }).then(r => r.data),
  void: (id: string, reason: string) =>
    client.post<LeaveEncashmentRequest>(`/api/leave/encashment/${id}/void`, { reason }).then(r => r.data),
  reject: (id: string, notes: string) =>
    client.post<LeaveEncashmentRequest>(`/api/leave/encashment/${id}/reject`, { notes }).then(r => r.data),
};

export const compOffApi = {
  list: (params: { employeeId?: number; status?: string; companyId?: string; branchId?: string } = {}) =>
    client.get<PagedResult<CompOffCredit>>('/api/leave/compoff', { params }).then(r => r.data.items ?? []),
  create: (body: { employeeId: number; workedDate: string; workType: string; hoursWorked: number; daysEarned: number; expiryDate?: string }) =>
    client.post<CompOffCredit>('/api/leave/compoff', body).then(r => r.data),
  approve: (id: string, notes?: string) =>
    client.post<CompOffCredit>(`/api/leave/compoff/${id}/approve`, { notes }).then(r => r.data),
  use: (id: string, daysToUse: number, leaveRequestId?: string) =>
    client.post(`/api/leave/compoff/${id}/use`, { daysToUse, leaveRequestId }).then(r => r.data),
};

export const absenceApi = {
  list: (params: { employeeId?: number; from?: string; to?: string; type?: string; companyId?: string; branchId?: string } = {}) =>
    client.get<PagedResult<AbsenceRecord>>('/api/leave/absences', { params }).then(r => r.data.items ?? []),
  record: (body: { employeeId: number; absenceDate: string; absenceType: string; payrollImpact?: string }) =>
    client.post<AbsenceRecord>('/api/leave/absences', body).then(r => r.data),
  listRegularizations: (params: { status?: string } = {}) =>
    client.get<PagedResult<AbsenceRegularizationRequest>>('/api/leave/absences/regularization', { params }).then(r => r.data.items ?? []),
  submitRegularization: (body: { employeeId: number; absenceRecordId: string; reason: string; leaveTypeId?: string }) =>
    client.post<AbsenceRegularizationRequest>('/api/leave/absences/regularization', body).then(r => r.data),
  approveRegularization: (id: string, notes?: string) =>
    client.post<AbsenceRegularizationRequest>(`/api/leave/absences/regularization/${id}/approve`, { notes }).then(r => r.data),
  rejectRegularization: (id: string, notes: string) =>
    client.post<AbsenceRegularizationRequest>(`/api/leave/absences/regularization/${id}/reject`, { notes }).then(r => r.data),
};

export const delegationApi = {
  list: (params: { employeeId?: number; status?: string } = {}) =>
    client.get<LeaveDelegation[]>('/api/leave/delegation', { params }).then(r => r.data),
  create: (body: { employeeId: number; employeeName: string; delegateEmployeeId: number; delegateEmployeeName: string; startDate: string; endDate: string; delegationType?: string; notes?: string; leaveRequestId?: string }) =>
    client.post<LeaveDelegation>('/api/leave/delegation', body).then(r => r.data),
  end: (id: string) =>
    client.post<LeaveDelegation>(`/api/leave/delegation/${id}/end`).then(r => r.data),
  cancel: (id: string) =>
    client.post<LeaveDelegation>(`/api/leave/delegation/${id}/cancel`).then(r => r.data),
};

export const leaveCalendarApi = {
  entries: (params: { fromDate: string; toDate: string; departmentName?: string; employeeId?: number; companyId?: string; branchId?: string } = { fromDate: '', toDate: '' }) =>
    client.get<LeaveCalendarEntry[]>('/api/leave/calendar', { params }).then(r => r.data),
  team: (params: { fromDate: string; toDate: string } = { fromDate: '', toDate: '' }) =>
    client.get<Record<string, LeaveCalendarEntry[]>>('/api/leave/calendar/team', { params }).then(r => r.data),
  today: () =>
    client.get('/api/leave/calendar/today').then(r => {
      const d = r.data;
      return (Array.isArray(d) ? d : (d as { employees?: LeaveCalendarEntry[] }).employees ?? []) as LeaveCalendarEntry[];
    }),
};


export const leaveReportsApi = {
  balanceSummary: (year: number, companyId?: string, branchId?: string) =>
    client.get<EmployeeLeaveBalance[]>('/api/leave/reports/balance-summary', { params: { year, companyId, branchId } }).then(r => r.data),
  usage: (params: { from: string; to: string; departmentName?: string; companyId?: string; branchId?: string } = { from: '', to: '' }) =>
    client.get<{ leaveTypeName: string; totalDays: number; count: number; departmentName: string }[]>('/api/leave/reports/usage', { params }).then(r => r.data),
  onLeaveToday: (params: { companyId?: string; branchId?: string } = {}) =>
    client.get<{ employees: LeaveRequest[] }>('/api/leave/reports/on-leave-today', { params }).then(r => r.data.employees ?? []),
  pendingApprovals: (params: { companyId?: string; branchId?: string } = {}) =>
    client.get<{ status: string; count: number }[]>('/api/leave/reports/pending-approvals', { params }).then(r => r.data),
  sickLeaveTrend: (params: { companyId?: string; branchId?: string } = {}) =>
    client.get<{ month: string; count: number; totalDays: number }[]>('/api/leave/reports/sick-leave-trend', { params }).then(r => r.data),
  liability: (year: number, companyId?: string, branchId?: string) =>
    client.get<{ employeeName: string; leaveTypeName: string; balanceDays: number; dailySalary: number; liabilityAmount: number }[]>('/api/leave/reports/liability', { params: { year, companyId, branchId } }).then(r => r.data),
  dashboard: (params: { companyId?: string; branchId?: string } = {}) =>
    client.get<LeaveDashboard>('/api/leave/reports/dashboard', { params }).then(r => r.data),
};

export const leaveAIApi = {
  list: (params: { type?: string; severity?: string; acknowledged?: boolean } = {}) =>
    client.get<PagedResult<LeaveAIInsight>>('/api/leave/ai-insights', { params }).then(r => r.data.items ?? []),
  generate: () =>
    client.post('/api/leave/ai-insights/generate').then(r => r.data),
  acknowledge: (id: string) =>
    client.post<LeaveAIInsight>(`/api/leave/ai-insights/${id}/acknowledge`).then(r => r.data),
};
