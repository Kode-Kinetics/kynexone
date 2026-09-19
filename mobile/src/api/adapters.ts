/**
 * Service adapters
 * These wrap the raw API services to provide the interface expected by screens.
 * Keeps screens decoupled from backend response shape changes.
 */

import {
  overtimeApi as _overtimeApi,
  payslipApi as _payslipApi,
  profileApi as _profileApi,
  attendanceApi as _attendanceApi,
  authApi as _authApi,
  hrRequestsApi as _hrRequestsApi,
  notificationsApi as _notificationsApi,
  teamApi as _teamApi,
} from './services';

// ─── Overtime ─────────────────────────────────────────────────────────────────
export const overtimeApi = {
  create: (payload: { date: string; startTime: string; endTime: string; reason: string }) =>
    _overtimeApi.submitOTRequest(payload),

  getMy: async (params: { page: number; limit: number }) => {
    const items = await _overtimeApi.getMyOTRequests();
    return { items: items ?? [] };
  },

  // There is no server-side preview endpoint; duration is derived client-side
  // (overnight blocks wrap past midnight, matching submitOTRequest).
  calculate: async (payload: { date: string; startTime: string; endTime: string }) => {
    const [sh, sm] = payload.startTime.split(':').map(Number);
    const [eh, em] = payload.endTime.split(':').map(Number);
    let minutes = eh * 60 + em - (sh * 60 + sm);
    if (minutes <= 0) minutes += 24 * 60;
    return { hours: minutes / 60, estimatedAmount: undefined as number | undefined };
  },
};

// ─── Payslips ─────────────────────────────────────────────────────────────────
export const payslipApi = {
  getList: async (params: { page: number; limit: number }) => {
    const items = await _payslipApi.getPayslips();
    return { items: items ?? [] };
  },

  getDetail: (id: string) => _payslipApi.getPayslipDetail(id),

  download: (id: string) => _payslipApi.downloadPayslip(id),
};

// ─── Profile ──────────────────────────────────────────────────────────────────
export const profileApi = {
  getMyProfile: () => _profileApi.getProfile(),

  // Backend allow-list (EmployeeSelfServiceController.AllowedSelfServiceProfileFields):
  // preferredName, personalEmail, phone, maritalStatus, emergencyContactName, emergencyContactPhone.
  requestUpdate: (changes: {
    preferredName?: string;
    personalEmail?: string;
    phone?: string;
    emergencyContactName?: string;
    emergencyContactPhone?: string;
  }) => _profileApi.requestProfileUpdate(changes),

  uploadPhoto: _profileApi.uploadProfilePhoto,
  photoSource: _profileApi.photoSource,
};

// ─── Attendance ───────────────────────────────────────────────────────────────
export const attendanceApi = {
  regularize: (payload: {
    date: string;
    requestedClockIn?: string;
    requestedClockOut?: string;
    reason: string;
  }) =>
    _attendanceApi.submitRegularization({
      date: payload.date,
      requestedClockIn: payload.requestedClockIn,
      requestedClockOut: payload.requestedClockOut,
      reason: payload.reason,
    }),
};

// ─── Auth ─────────────────────────────────────────────────────────────────────
export const authApi = {
  changePassword: (payload: { currentPassword: string; newPassword: string }) =>
    _authApi.changePassword(payload.currentPassword, payload.newPassword),

  forgotPassword: (payload: { email: string; tenantSlug?: string }) =>
    _authApi.forgotPassword(payload.email, payload.tenantSlug),

  resetPassword: (payload: { email: string; token: string; newPassword: string; tenantSlug?: string }) =>
    _authApi.resetPassword(payload.email, payload.token, payload.newPassword, payload.tenantSlug),
};

// ─── HR Requests ──────────────────────────────────────────────────────────────
export const hrRequestsApi = {
  getMy: async (params: { page: number; limit: number }) => {
    const items = await _hrRequestsApi.getMyRequests();
    return { items: items ?? [] };
  },

  getDetail: (id: string) => _hrRequestsApi.getRequestDetail(id),

  create: (payload: { requestType: string; subject: string; description: string; attachmentDocumentId?: string }) =>
    _hrRequestsApi.createRequest(payload),

  addComment: (id: string, content: string) =>
    _hrRequestsApi.addComment(id, content),
};

// ─── Notifications ───────────────────────────────────────────────────────────
export const notificationsApi = {
  getAll: async (params: { page: number; limit: number }) => {
    const items = await _notificationsApi.getNotifications();
    return { items: items ?? [] };
  },

  markRead: (id: string) => _notificationsApi.markRead(id),
};

// ─── Team ─────────────────────────────────────────────────────────────────────
export const teamApi = {
  getTeam: async (params: { date?: string }) => {
    const items = await _teamApi.getTeamMembers();
    return { items: items ?? [] };
  },
};

// ─── Re-export unchanged services ────────────────────────────────────────────
export {
  dashboardApi,
  leaveApi,
  documentsApi,
  policiesApi,
  approvalsApi,
  aiApi,
  deviceApi,
} from './services';
