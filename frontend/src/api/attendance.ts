import client from './client';
import type { PagedResult } from './organization';

/** One attendance rule set: what counts as late, an early exit or a half day, and the length of the working day. */
export interface AttendancePolicy {
  id: string;
  code: string;
  name: string;
  branchId: string | null;
  departmentId: string | null;
  gradeId: string | null;
  graceMinutes: number;
  earlyExitThresholdMinutes: number;
  halfDayThresholdMinutes: number;
  standardWorkMinutes: number;
  breakMinutes: number;
  isActive: boolean;
}

export type AttendancePolicyRequest = Omit<AttendancePolicy, 'id' | 'isActive'>;

/** The day an attendance exception is about (GET /api/attendance/exceptions). */
export interface AttendanceExceptionDay {
  status: string;
  firstInUtc?: string | null;
  lastOutUtc?: string | null;
  lateMinutes: number;
  earlyExitMinutes: number;
  missingPunch: boolean;
}

/** One exception on Attendance → Exceptions. */
export interface AttendanceExceptionItem {
  id: string;
  employeeId: number;
  employeeName: string;
  employeeCode: string;
  workDate: string;
  /** MissingPunch | PaidAttendanceChanged | ImportedDayHasPunches */
  exceptionType: string;
  severity: string;
  description: string;
  isResolved: boolean;
  resolution?: string | null;
  createdAtUtc: string;
  day?: AttendanceExceptionDay | null;
}

export interface AttendanceDevice {
  id: string;
  deviceName: string;
  deviceType: string;
  vendor: string;
  serialNumber: string;
  branchId?: string;
  locationName: string;
  ipAddress: string;
  endpointUrl: string;
  port?: number;
  apiKeyReference: string;
  syncMethod: string;
  syncFrequency: string;
  // Flexible config fields (added via MissingTableCreator — may be absent on older records)
  authType?: string;
  authCredentialsJson?: string;
  customHeadersJson?: string;
  deviceParametersJson?: string;
  fieldMappingsJson?: string;
  notes?: string;
  lastSyncStatus: string;
  lastSyncAtUtc?: string;
  errorLog: string;
  isActive: boolean;
}

export interface AttendanceDeviceRequest {
  deviceName: string;
  deviceType: string;
  vendor: string;
  serialNumber: string;
  branchId?: string;
  locationName?: string;
  ipAddress?: string;
  endpointUrl?: string;
  port?: number;
  apiKeyReference?: string;
  syncMethod?: string;
  syncFrequency?: string;
  authType?: string;
  authCredentialsJson?: string;
  customHeadersJson?: string;
  deviceParametersJson?: string;
  fieldMappingsJson?: string;
  notes?: string;
  isActive: boolean;
}

export interface AttendanceDeviceSyncLog {
  id: string;
  deviceId?: string;
  syncMethod: string;
  status: string;
  startedAtUtc: string;
  completedAtUtc?: string;
  rawEventsReceived: number;
  rawEventsProcessed: number;
  errorMessage: string;
}

export interface DeviceKeyResult {
  id: string;
  deviceName: string;
  key: string;
}

export interface AttendanceRawEvent {
  id: string;
  employeeId?: number;
  employeeCode: string;
  deviceId?: string;
  source: string;
  punchTimestampUtc: string;
  punchDirection: string;
  locationName: string;
  latitude?: number;
  longitude?: number;
  ipAddress: string;
  verificationMethod: string;
  confidenceScore?: number;
  isProcessed: boolean;
  createdAtUtc: string;
  /** A stored selfie (not yet deleted under retention) backs this punch; HR opens it with evidence.selfie(id). */
  hasSelfie?: boolean;
}

export interface AttendanceRawEventRequest {
  employeeId?: number;
  employeeCode?: string;
  deviceId?: string;
  source?: string;
  punchTimestampUtc: string;
  punchDirection?: string;
  locationName?: string;
  latitude?: number;
  longitude?: number;
  ipAddress?: string;
  photoReference?: string;
  rawPayloadJson?: string;
  syncBatchReference?: string;
  verificationMethod?: string;
  confidenceScore?: number;
}

export interface AttendanceDailyRecord {
  id: string;
  employeeId: number;
  employeeName: string;
  department: string;
  branch: string;
  workDate: string;
  firstInUtc?: string;
  lastOutUtc?: string;
  totalWorkedMinutes: number;
  lateMinutes: number;
  earlyExitMinutes: number;
  overtimeMinutes: number;
  undertimeMinutes: number;
  missingPunch: boolean;
  status: string;
  manualCorrectionStatus: string;
  isPayrollLocked: boolean;
}

export interface AttendanceDashboardSummary {
  date: string;
  activeEmployees: number;
  present: number;
  absent: number;
  late: number;
  missingPunch: number;
  overtimeEmployees: number;
  deviceErrors: number;
  pendingRegularizations: number;
}

export interface AttendanceTodaySummary {
  date: string;
  totalActive: number;
  present: number;
  absent: number;
  onLeave: number;
  late: number;
}

export interface AttendanceImportBatch {
  id: string;
  fileName: string;
  source: string;
  status: string;
  totalRows: number;
  importedRows: number;
  failedRows: number;
  createdAtUtc: string;
}

export interface AttendanceRegularizationRequest {
  id: string;
  employeeId: number;
  workDate: string;
  requestType: string;
  requestedInUtc?: string;
  requestedOutUtc?: string;
  reason: string;
  status: string;
  payrollLockChecked: boolean;
  createdAtUtc: string;
}

export interface AttendanceMonthlySummary {
  employeeId: number;
  employeeName: string;
  presentDays: number;
  absentDays: number;
  lateDays: number;
  missingPunchDays: number;
  overtimeMinutes: number;
}

export interface AttendancePayrollSummary {
  employeeId: number;
  employeeName: string;
  lateMinutes: number;
  earlyExitMinutes: number;
  absenceDays: number;
  overtimeMinutes: number;
  hasLockedRecords: boolean;
}

export interface AttendanceDeviceSyncSummary {
  deviceId: string;
  deviceName: string;
  vendor: string;
  status: string;
  lastSyncAtUtc?: string;
  errorLog: string;
}

export interface AttendanceAIInsight {
  id: string;
  insightType: string;
  severity: string;
  title: string;
  summary: string;
  employeeId?: number;
  createdAtUtc: string;
}

export const attendanceApi = {
  dashboard: (date?: string) =>
    client.get<AttendanceDashboardSummary>('/api/attendance/dashboard', { params: { date } }).then((r) => r.data),

  todaySummary: () => client.get<AttendanceTodaySummary>('/api/attendance/today').then((r) => r.data),

  daily: (params: { employeeId?: number; from?: string; to?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<AttendanceDailyRecord>>('/api/attendance/daily', { params }).then((r) => r.data),

  monthly: (params: { year?: number; month?: number; employeeId?: number } = {}) =>
    client.get<AttendanceMonthlySummary[]>('/api/attendance/monthly', { params }).then((r) => r.data),

  process: (data: { fromDate: string; toDate: string; employeeId?: number }) =>
    client.post<number>('/api/attendance/process', data).then((r) => r.data),

  reprocess: (data: { fromDate: string; toDate: string; employeeId?: number }) =>
    client.post<number>('/api/attendance/reprocess', data).then((r) => r.data),

  /** Setup → Attendance rules (AttendancePoliciesController). Admin and HR Manager only. */
  policies: {
    list: () => client.get<AttendancePolicy[]>('/api/attendance/policies').then((r) => r.data),
    create: (data: AttendancePolicyRequest) => client.post<AttendancePolicy>('/api/attendance/policies', data).then((r) => r.data),
    update: (id: string, data: AttendancePolicyRequest) => client.put<AttendancePolicy>(`/api/attendance/policies/${id}`, data).then((r) => r.data),
    deactivate: (id: string) => client.post<AttendancePolicy>(`/api/attendance/policies/${id}/deactivate`).then((r) => r.data),
    reactivate: (id: string) => client.post<AttendancePolicy>(`/api/attendance/policies/${id}/reactivate`).then((r) => r.data),
  },

  devices: {
    list: (params: { page?: number; pageSize?: number } = {}) =>
      client.get<PagedResult<AttendanceDevice>>('/api/attendance/devices', { params }).then((r) => r.data),
    create: (data: AttendanceDeviceRequest) =>
      client.post<AttendanceDevice>('/api/attendance/devices', data).then((r) => r.data),
    update: (id: string, data: AttendanceDeviceRequest) =>
      client.put<AttendanceDevice>(`/api/attendance/devices/${id}`, data).then((r) => r.data),
    remove: (id: string) => client.delete(`/api/attendance/devices/${id}`).then((r) => r.data),
    test: (id: string) =>
      client.post<AttendanceDeviceSyncLog>(`/api/attendance/devices/${id}/test-connection`).then((r) => r.data),
    sync: (id: string) =>
      client.post<AttendanceDeviceSyncLog>(`/api/attendance/devices/${id}/sync`).then((r) => r.data),
    logs: (id: string) =>
      client.get<AttendanceDeviceSyncLog[]>(`/api/attendance/devices/${id}/sync-logs`).then((r) => r.data),
    generateKey: (id: string) =>
      client.post<DeviceKeyResult>(`/api/attendance/devices/${id}/generate-key`).then((r) => r.data),
  },

  events: {
    push: (data: AttendanceRawEventRequest) =>
      client.post<AttendanceRawEvent>('/api/attendance/events/push', data).then((r) => r.data),
    importCsv: (data: { fileName: string; csvContent: string }) =>
      client.post<AttendanceImportBatch>('/api/attendance/events/import', data).then((r) => r.data),
    raw: (params: { employeeId?: number; from?: string; to?: string; processed?: boolean; page?: number; pageSize?: number } = {}) =>
      client.get<PagedResult<AttendanceRawEvent>>('/api/attendance/events/raw', { params }).then((r) => r.data),
  },

  evidence: {
    /** HR's review view: the JPEG behind one punch (attendance.evidence.view; audited; never the caller's own punch). */
    selfie: (rawEventId: string) =>
      client.get<Blob>(`/api/attendance/evidence/${encodeURIComponent(rawEventId)}/selfie`, { responseType: 'blob' }).then((r) => r.data),
  },

  punch: {
    web: (data: { employeeId: number; punchDirection: string; locationName?: string; latitude?: number; longitude?: number }) =>
      client.post<AttendanceRawEvent>('/api/attendance/punch/web', data).then((r) => r.data),
    mobile: (data: { employeeId: number; punchDirection: string; locationName?: string; latitude?: number; longitude?: number }) =>
      client.post<AttendanceRawEvent>('/api/attendance/punch/mobile', data).then((r) => r.data),
    kiosk: (data: { employeeId: number; punchDirection: string; locationName?: string; latitude?: number; longitude?: number }) =>
      client.post<AttendanceRawEvent>('/api/attendance/punch/kiosk', data).then((r) => r.data),
  },

  regularization: {
    create: (data: { employeeId: number; workDate: string; requestType: string; requestedInUtc?: string; requestedOutUtc?: string; reason: string }) =>
      client.post<AttendanceRegularizationRequest>('/api/attendance/regularization', data).then((r) => r.data),
    mine: (params: { employeeId?: number; page?: number; pageSize?: number } = {}) =>
      client.get<PagedResult<AttendanceRegularizationRequest>>('/api/attendance/regularization/my', { params }).then((r) => r.data),
    pending: (params: { page?: number; pageSize?: number } = {}) =>
      client.get<PagedResult<AttendanceRegularizationRequest>>('/api/attendance/regularization/pending-approval', { params }).then((r) => r.data),
    approve: (id: string, comments: string) =>
      client.post<AttendanceRegularizationRequest>(`/api/attendance/regularization/${id}/approve`, { comments }).then((r) => r.data),
    reject: (id: string, comments: string) =>
      client.post<AttendanceRegularizationRequest>(`/api/attendance/regularization/${id}/reject`, { comments }).then((r) => r.data),
  },

  reports: {
    daily: (from: string, to: string) =>
      client.get<AttendanceDailyRecord[]>('/api/attendance/reports/daily', { params: { from, to } }).then((r) => r.data),
    late: (from: string, to: string) =>
      client.get<AttendanceDailyRecord[]>('/api/attendance/reports/late', { params: { from, to } }).then((r) => r.data),
    absence: (from: string, to: string) =>
      client.get<AttendanceDailyRecord[]>('/api/attendance/reports/absence', { params: { from, to } }).then((r) => r.data),
    missingPunch: (from: string, to: string) =>
      client.get<AttendanceDailyRecord[]>('/api/attendance/reports/missing-punch', { params: { from, to } }).then((r) => r.data),
    payrollSummary: (from: string, to: string) =>
      client.get<AttendancePayrollSummary[]>('/api/attendance/reports/payroll-summary', { params: { from, to } }).then((r) => r.data),
    deviceSync: () =>
      client.get<AttendanceDeviceSyncSummary[]>('/api/attendance/reports/device-sync').then((r) => r.data),
  },

  aiInsights: () => client.get<AttendanceAIInsight[]>('/api/attendance/ai/insights').then((r) => r.data),

  exceptions: (params: { state?: 'open' | 'resolved' | 'all'; type?: string; from?: string; to?: string; employeeId?: number; page?: number; pageSize?: number } = {}) =>
    client.get<PagedResult<AttendanceExceptionItem>>('/api/attendance/exceptions', { params }).then((r) => r.data),

  /** The reason is required; the resolution is kept with the exception. */
  resolveException: (id: string, reason: string) =>
    client.post<AttendanceExceptionItem>(`/api/attendance/exceptions/${id}/resolve`, { reason }).then((r) => r.data),
};
