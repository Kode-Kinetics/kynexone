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
  create: (payload: {
    date: string;
    startTime: string;
    endTime: string;
    reason: string;
    endsNextDay: boolean;
    timeZone: string;
  }) => _overtimeApi.submitOTRequest(payload),

  getMy: async () => ({ items: (await _overtimeApi.getMyOTRequests()) ?? [] }),

  /** null when the policy is unknown (none, or more than one active). */
  getPolicyLimits: () => _overtimeApi.getPolicyLimits(),

  getMyPay: () => _overtimeApi.getMyPay(),
};

// ─── Payslips ─────────────────────────────────────────────────────────────────
export const payslipApi = {
  getList: async () => ({ items: (await _payslipApi.getPayslips()) ?? [] }),

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

  resetPassword: (payload: { token: string; newPassword: string; tenantSlug: string }) =>
    _authApi.resetPassword(payload.token, payload.newPassword, payload.tenantSlug),

  acceptInvitation: (payload: { token: string; newPassword: string; tenantSlug: string }) =>
    _authApi.acceptInvitation(payload.token, payload.newPassword, payload.tenantSlug),
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
