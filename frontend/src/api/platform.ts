import axios from 'axios';

const platform = axios.create({ baseURL: '' });

platform.interceptors.request.use(cfg => {
  const token = typeof window !== 'undefined' ? localStorage.getItem('platform_access_token') : null;
  if (token) cfg.headers.Authorization = `Bearer ${token}`;
  return cfg;
});

// 401 → token expired → clear + redirect to login
platform.interceptors.response.use(
  res => res,
  err => {
    if (err.response?.status === 401 && typeof window !== 'undefined') {
      localStorage.removeItem('platform_access_token');
      if (!window.location.pathname.startsWith('/platform/login')) {
        window.location.replace('/platform/login');
      }
    }
    return Promise.reject(err);
  }
);

export default platform;

// ── Types ─────────────────────────────────────────────────────────────────────

export interface PlatformStats {
  totalTenants: number;
  activeTenants: number;
  totalUsers: number;
  totalEmployees: number;
  estimatedMrr: number;
  expiringCount: number;
  suspendedCount: number;
  overdueCount: number;
  tenantsByPlan: {
    trial: number;
    starter: number;
    growth: number;
    enterprise: number;
  };
}

export interface CreateTenantBody {
  name: string;
  slug: string;
  adminEmail: string;
  adminFullName?: string;
  adminPassword: string;
  plan?: string;
  maxUsers?: number | null;
  maxEmployees?: number | null;
  maxCompanies?: number | null;
  maxAdminUsers?: number | null;
  billingEmail?: string;
  billingCycle?: string;
  monthlyAmount?: number;
  currencyCode?: string;
  expiresAtUtc?: string | null;
}

export interface CreateTenantResult {
  tenantId: string;
  name: string;
  slug: string;
  adminUserId: string;
  adminEmail: string;
  plan: string;
  loginHint: string;
}

export interface TenantAdminUser {
  id: string;
  email: string;
  fullName: string;
  isActive: boolean;
  status: string;
  createdAtUtc: string;
}

export interface TenantUser {
  id: string;
  email: string;
  fullName: string;
  isActive: boolean;
  isLocked: boolean;
  lockoutEnd: string | null;
  mFAEnabled: boolean;
  mustChangePassword: boolean;
  status: string;
  createdAtUtc: string;
  roles: string[];
}

export interface TenantRole {
  id: string;
  name: string;
  description: string;
  isSystem: boolean;
}

export interface PlatformTenantSummary {
  id: string;
  name: string;
  slug: string;
  isActive: boolean;
  createdAtUtc: string;
  subscription: {
    plan: string;
    status: string;
    maxEmployees: number;
    maxUsers: number;
    expiresAtUtc: string | null;
    monthlyAmount: number;
    currencyCode: string;
    billingEmail: string;
    billingCycle: string;
  } | null;
  activeUserCount: number;
  activeEmployeeCount: number;
  isDeleted?: boolean;
}

export interface TenantBranding {
  logoUrl: string;
  faviconUrl: string;
  primaryColor: string;
  accentColor: string;
  portalTitle: string;
  companyNameEn: string;
  companyNameAr: string;
  updatedAtUtc?: string;
}

export interface TenantLocalization {
  defaultLanguage: string;
  rtlEnabled: boolean;
  calendarSystem: string;
  defaultTimezone: string;
  dateFormat: string;
  currencyCode: string;
  countryCode: string;
  weekStartDay: string;
  workWeek: string;
  hijriDatesEnabled: boolean;
  updatedAtUtc?: string;
}

export interface PlatformTenantDetail {
  id: string;
  name: string;
  slug: string;
  isActive: boolean;
  subscription: {
    plan: string;
    status: string;
    maxEmployees: number;
    maxUsers: number;
    maxCompanies?: number;
    maxAdminUsers?: number;
    billingEmail: string;
    billingCycle: string;
    monthlyAmount: number;
    currencyCode: string;
    startedAtUtc: string;
    expiresAtUtc: string | null;
  };
  featureFlags: Array<{ featureKey: string; isEnabled: boolean }>;
  localization: TenantLocalization | null;
  branding: TenantBranding | null;
  userCount: number;
  employeeCount: number;
}

export interface BulkOpResultItem {
  tenantId: string;
  name: string | null;
  status: 'ok' | 'skipped' | 'failed';
  reason: string | null;
}

export interface BulkOpResult {
  operation: string;
  featureKey: string | null;
  appliedToAll: boolean | null;
  requested: number;
  succeeded: number;
  skipped: number;
  failed: number;
  results: BulkOpResultItem[];
}

export interface PlatformAuditLog {
  id: string;
  tenantId: string;
  tenantName?: string;
  entityType: string;
  entityId: string;
  action: string;
  oldValuesJson: string;
  newValuesJson: string;
  performedByName: string;
  ipAddress: string;
  createdAtUtc: string;
}

export interface PasswordResetResult {
  userId: string;
  userEmail: string;
  message: string;
  emailDeliveryAvailable: boolean;
  emailSent: boolean;
  resetTokenExpiresAt?: string;
}

export interface PlatformPlan {
  name: string;
  maxUsers: number;
  maxEmployees: number;
  monthlyPrice: number;
  description: string;
}

export interface TenantInvoice {
  id: string;
  tenantId: string;
  invoiceNumber: string;
  amount: number;
  currencyCode: string;
  status: 'Draft' | 'Sent' | 'Paid' | 'PartiallyPaid' | 'Overdue' | 'Cancelled';
  paymentMethod: string | null;
  paymentReference: string | null;
  periodDescription: string | null;
  invoiceDate: string;
  dueDate: string;
  paidDate: string | null;
  notes: string | null;
  recipientEmail: string | null;
  createdAtUtc: string;
}

export interface TenantInvoiceLine {
  id: string;
  invoiceId: string;
  tenantId: string;
  description: string;
  quantity: number;
  unitPrice: number;
  discountAmount: number;
  taxRate: number;
  taxAmount: number;
  lineTotal: number;
  sortOrder: number;
  createdAtUtc: string;
}

export interface TenantPayment {
  id: string;
  tenantId: string;
  invoiceId: string | null;
  amount: number;
  currencyCode: string;
  method: string;
  reference: string | null;
  status: 'Pending' | 'Completed' | 'Failed' | 'Refunded';
  paidAt: string | null;
  receivedByPlatformUserId: string | null;
  notes: string | null;
  createdAtUtc: string;
}

export interface LoginActivity {
  id: string;
  tenantId: string | null;
  userId: string | null;
  emailAttempted: string | null;
  eventType: string;
  failureReason: string | null;
  ipAddress: string | null;
  userAgent: string | null;
  occurredAtUtc: string;
}

export interface TenantAiUsage {
  tenantId: string;
  plan: string;
  yearMonth: number;
  tokensUsed: number;
  requestCount: number;
  blockedCount: number;
  monthlyTokenLimit: number;
  isUnlimited: boolean;
  usagePct: number;
}

export interface SupportSession {
  id: string;
  tenantId: string;
  targetUserId: string;
  targetUserEmail: string;
  reason: string;
  startedByEmail: string;
  startedByIp: string;
  startedAtUtc: string;
  expiresAtUtc: string;
  endedAtUtc: string | null;
  isActive: boolean;
}

export interface StartSupportAccessResult {
  sessionId: string;
  token: string;
  expiresAt: string;
  targetUserEmail: string;
  tenantSlug: string;
  reason: string;
}

// ── Platform API ──────────────────────────────────────────────────────────────

export interface PlatformAnnouncement {
  id: string;
  title: string;
  body: string;
  targetPlan: string;
  status: 'Draft' | 'Published' | 'Archived';
  publishedAtUtc: string | null;
  expiresAtUtc: string | null;
  createdByEmail: string;
  createdAtUtc: string;
}

export interface PlatformLead {
  id: string;
  companyName: string;
  contactName: string;
  contactEmail: string;
  phone: string | null;
  message: string | null;
  status: 'New' | 'Contacted' | 'DemoScheduled' | 'Converted' | 'Lost';
  notes: string | null;
  assignedTo: string | null;
  source: string;
  createdAtUtc: string;
  updatedAtUtc: string | null;
  convertedToTenantId: string | null;
}

export interface PlatformSettings {
  smtp: {
    host: string;
    port: number;
    username: string;
    fromEmail: string;
    fromName: string;
    useSsl: boolean;
    isConfigured: boolean;
  };
  trial: { durationDays: number };
  branding: { platformName: string; supportEmail: string };
}

export interface BillingSummary {
  totalMrr: number;
  totalArr: number;
  overdueTotalAmount: number;
  overdueCount: number;
  totalInvoices: number;
  paidThisMonth: number;
  sentThisMonth: number;
}

export interface TenantSecurityPosture {
  tenantId: string;
  tenantName: string;
  tenantSlug: string;
  hasMfaEnabled: boolean;
  hasSecurityPolicy: boolean;
  riskLevel: 'Low' | 'Medium' | 'High';
  lastSecurityEvent: string | null;
}

export interface PlatformTeamMember {
  id: string;
  email: string;
  fullName: string;
  role: string;
  isActive: boolean;
  lastLoginAtUtc: string | null;
  lastLoginIp: string | null;
  createdAtUtc: string;
  updatedAtUtc: string | null;
}

export interface PlatformHealthStatus {
  status: 'healthy' | 'degraded' | 'error';
  components: {
    database: { status: 'ok' | 'error' | 'unknown' };
    smtp:     { status: 'configured' | 'not_configured' | 'unknown' };
    redis:    { status: 'ok' | 'error' | 'unknown' | 'not_configured' | 'disconnected' };
    jobs:     { status: 'ok' | 'error' | 'unknown' };
  };
  version: string;
  environment: string;
  checkedAtUtc: string;
}

export interface TenantSecurityPolicy {
  tenantId: string;
  tenantName: string;
  passwordMinLength: number;
  passwordRequireUppercase: boolean;
  passwordRequireLowercase: boolean;
  passwordRequireDigit: boolean;
  passwordRequireSpecial: boolean;
  passwordExpiryDays: number;
  passwordHistoryCount: number;
  maxFailedLoginAttempts: number;
  lockoutDurationMinutes: number;
  sessionTimeoutMinutes: number;
  refreshTokenExpiryDays: number;
  allowMultipleSessions: boolean;
  isCustomPolicy: boolean;
  updatedAtUtc?: string;
}

export const PLATFORM_ROLES = ['Owner', 'Admin', 'Finance', 'Support', 'Marketing', 'Auditor'] as const;
export type PlatformRole = typeof PLATFORM_ROLES[number];

export interface PlatformPricingConfig {
  key: string;
  label: string;
  group: string;
  plan: string;
  value: number;
  updatedAtUtc: string;
}

export interface PlatformPricingModule {
  moduleKey: string;
  moduleName: string;
  includedInTrial: boolean;
  includedInStarter: boolean;
  includedInGrowth: boolean;
  includedInEnterprise: boolean;
  isEnterpriseOnly: boolean;
  addonPriceMonthly: number;
  sortOrder: number;
}

export interface PlatformQuote {
  id: string;
  companyName: string;
  contactName: string;
  contactEmail: string;
  phone?: string;
  orgType: string;
  numCompanies: number;
  numBranches: number;
  numEmployees: number;
  numAdminUsers: number;
  numCountries: number;
  needsArabic: boolean;
  selectedModulesJson: string;
  estimatedMonthlyAmount: number;
  estimatedAnnualAmount: number;
  notes?: string;
  status: string;
  convertedToTenantId?: string;
  createdAtUtc: string;
  updatedAtUtc?: string;
}

export const platformApi = {
  login: (email: string, password: string) =>
    platform.post<{ token: string }>('/api/platform/auth/login', { email, password }).then(r => r.data),

  getStats: () =>
    platform.get<PlatformStats>('/api/platform/stats').then(r => r.data),

  listTenants: () =>
    platform.get<PlatformTenantSummary[]>('/api/platform/tenants').then(r => r.data),

  getTenant: (tenantId: string) =>
    platform.get<PlatformTenantDetail>(`/api/platform/tenants/${tenantId}`).then(r => r.data),

  updateTenant: (tenantId: string, name: string) =>
    platform.patch<{ id: string; name: string; slug: string }>(`/api/platform/tenants/${tenantId}`, { name }).then(r => r.data),

  updateSubscription: (tenantId: string, body: {
    plan: string;
    status: string;
    maxUsers: number;
    maxEmployees: number;
    maxCompanies: number;
    maxAdminUsers: number;
    billingEmail: string;
    billingCycle: string;
    monthlyAmount: number;
    currencyCode: string;
    startedAtUtc: string;
    expiresAtUtc: string | null;
  }) =>
    platform.put(`/api/platform/tenants/${tenantId}/subscription`, body).then(r => r.data),

  suspendTenant: (tenantId: string, reason: string) =>
    platform.post(`/api/platform/tenants/${tenantId}/suspend`, { reason }).then(r => r.data),

  reactivateTenant: (tenantId: string, reason: string) =>
    platform.post(`/api/platform/tenants/${tenantId}/reactivate`, { reason }).then(r => r.data),

  setFeature: (tenantId: string, featureKey: string, isEnabled: boolean) =>
    platform.put(`/api/platform/tenants/${tenantId}/features/${featureKey}`, { isEnabled }).then(r => r.data),

  impersonate: (tenantId: string, userId: string) =>
    platform.post<{ token: string }>(`/api/platform/tenants/${tenantId}/impersonate`, { userId }).then(r => r.data),

  createTenant: (body: CreateTenantBody) =>
    platform.post<CreateTenantResult>('/api/platform/tenants', body).then(r => r.data),

  listAdmins: (tenantId: string) =>
    platform.get<TenantAdminUser[]>(`/api/platform/tenants/${tenantId}/admins`).then(r => r.data),

  addAdmin: (tenantId: string, body: { email: string; fullName?: string; password: string }) =>
    platform.post<TenantAdminUser>(`/api/platform/tenants/${tenantId}/admins`, body).then(r => r.data),

  listTenantUsers: (tenantId: string, search?: string) =>
    platform.get<TenantUser[]>(`/api/platform/tenants/${tenantId}/users`, { params: search ? { search } : {} }).then(r => r.data),

  sendPasswordReset: (userId: string) =>
    platform.post<PasswordResetResult>(`/api/platform/users/${userId}/send-password-reset`).then(r => r.data),

  forcePasswordReset: (userId: string, tempPassword: string) =>
    platform.post(`/api/platform/users/${userId}/force-password-reset`, { tempPassword }).then(r => r.data),

  editUser: (userId: string, body: { fullName?: string; email?: string; status?: string; isActive?: boolean; roleName?: string }) =>
    platform.patch(`/api/platform/users/${userId}`, body).then(r => r.data),

  unlockUser: (userId: string) =>
    platform.post(`/api/platform/users/${userId}/unlock`).then(r => r.data),

  disableMfa: (userId: string) =>
    platform.post(`/api/platform/users/${userId}/disable-mfa`).then(r => r.data),

  revokeSessions: (userId: string) =>
    platform.post(`/api/platform/users/${userId}/revoke-sessions`).then(r => r.data),

  listTenantRoles: (tenantId: string) =>
    platform.get<TenantRole[]>(`/api/platform/tenants/${tenantId}/roles`).then(r => r.data),

  updateBranding: (tenantId: string, body: Partial<TenantBranding>) =>
    platform.put<TenantBranding>(`/api/platform/tenants/${tenantId}/branding`, body).then(r => r.data),

  updateLocalization: (tenantId: string, body: Partial<TenantLocalization>) =>
    platform.put<TenantLocalization>(`/api/platform/tenants/${tenantId}/localization`, body).then(r => r.data),

  deleteTenant: (tenantId: string) =>
    platform.delete(`/api/platform/tenants/${tenantId}?confirm=DELETE`).then(r => r.data),

  // ── Soft-delete lifecycle ─────────────────────────────────────────────────
  restoreTenant: (tenantId: string) =>
    platform.post(`/api/platform/tenants/${tenantId}/restore`).then(r => r.data),

  bulkRestoreTenants: (tenantIds: string[]) =>
    platform.post<BulkOpResult>('/api/platform/tenants/bulk/restore', { tenantIds }).then(r => r.data),

  /** GDPR hard-erase a soft-deleted tenant. Owner-only, irreversible. */
  purgeTenant: (tenantId: string) =>
    platform.delete(`/api/platform/tenants/${tenantId}/purge?confirm=PURGE`).then(r => r.data),

  // ── Bulk tenant operations ────────────────────────────────────────────────
  bulkSuspendTenants: (tenantIds: string[], reason?: string) =>
    platform.post<BulkOpResult>('/api/platform/tenants/bulk/suspend', { tenantIds, reason }).then(r => r.data),

  bulkReactivateTenants: (tenantIds: string[], reason?: string) =>
    platform.post<BulkOpResult>('/api/platform/tenants/bulk/reactivate', { tenantIds, reason }).then(r => r.data),

  bulkDeleteTenants: (tenantIds: string[]) =>
    platform.post<BulkOpResult>('/api/platform/tenants/bulk/delete', { tenantIds, confirm: 'DELETE' }).then(r => r.data),

  /** Enable/disable one feature across selected tenants, or platform-wide with applyToAll. */
  bulkSetFeature: (args: { tenantIds?: string[]; applyToAll?: boolean; featureKey: string; isEnabled: boolean }) =>
    platform.post<BulkOpResult>('/api/platform/tenants/bulk/features', {
      tenantIds: args.tenantIds ?? null,
      applyToAll: args.applyToAll ?? false,
      featureKey: args.featureKey,
      isEnabled: args.isEnabled,
    }).then(r => r.data),

  getAuditLogs: (tenantId?: string, page = 1, pageSize = 50, filters?: {
    action?: string;
    entityType?: string;
    from?: string;
    to?: string;
  }) =>
    platform.get<{ total: number; page: number; pageSize: number; logs: PlatformAuditLog[] }>(
      '/api/platform/audit-logs',
      { params: { ...(tenantId ? { tenantId } : {}), page, pageSize, ...filters } }
    ).then(r => r.data),

  getPlans: () =>
    platform.get<PlatformPlan[]>('/api/platform/plans').then(r => r.data),

  updatePlanPrice: (planName: string, monthlyPrice: number) =>
    platform.put<{ planName: string; monthlyPrice: number }>(`/api/platform/plans/${planName}/price`, { monthlyPrice }).then(r => r.data),

  downloadInvoicePdf: (tenantId: string, invoiceId: string, invoiceNumber: string) => {
    return platform.get(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/pdf`, { responseType: 'blob' })
      .then(r => {
        const url = window.URL.createObjectURL(new Blob([r.data], { type: 'application/pdf' }));
        const a = document.createElement('a');
        a.href = url;
        a.download = `Invoice_${invoiceNumber}.pdf`;
        a.click();
        window.URL.revokeObjectURL(url);
      });
  },

  sendInvoiceEmail: (tenantId: string, invoiceId: string) =>
    platform.post<{ sent: boolean; billingEmail: string; invoiceNumber: string; pdfAttached?: boolean; smtpRequired?: boolean; message?: string }>(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/send`).then(r => r.data),

  startSupportAccess: (tenantId: string, userId: string, reason: string) =>
    platform.post<StartSupportAccessResult>('/api/platform/support-access/start', { tenantId, userId, reason }).then(r => r.data),

  endSupportAccess: (sessionId: string) =>
    platform.post('/api/platform/support-access/end', { sessionId }).then(r => r.data),

  listSupportSessions: (tenantId?: string, activeOnly = false, page = 1, pageSize = 50) =>
    platform.get<{ total: number; page: number; pageSize: number; sessions: SupportSession[] }>(
      '/api/platform/support-access',
      { params: { ...(tenantId ? { tenantId } : {}), activeOnly, page, pageSize } }
    ).then(r => r.data),

  // ── Marketing ────────────────────────────────────────────────────────────────

  listAnnouncements: (status?: string) =>
    platform.get<PlatformAnnouncement[]>('/api/platform/marketing/announcements', { params: status ? { status } : {} }).then(r => r.data),

  createAnnouncement: (body: { title: string; body: string; targetPlan?: string; expiresAtUtc?: string | null }) =>
    platform.post<PlatformAnnouncement>('/api/platform/marketing/announcements', body).then(r => r.data),

  updateAnnouncement: (id: string, body: { title?: string; body?: string; status?: string; expiresAtUtc?: string | null }) =>
    platform.patch<PlatformAnnouncement>(`/api/platform/marketing/announcements/${id}`, body).then(r => r.data),

  deleteAnnouncement: (id: string) =>
    platform.delete(`/api/platform/marketing/announcements/${id}`).then(r => r.data),

  // ── Leads ─────────────────────────────────────────────────────────────────

  listLeads: (status?: string) =>
    platform.get<PlatformLead[]>('/api/platform/leads', { params: status ? { status } : {} }).then(r => r.data),

  createLead: (body: { companyName: string; contactName: string; contactEmail: string; phone?: string; message?: string; source?: string }) =>
    platform.post<PlatformLead>('/api/platform/leads', body).then(r => r.data),

  updateLead: (id: string, body: { status?: string; notes?: string; assignedTo?: string }) =>
    platform.patch<PlatformLead>(`/api/platform/leads/${id}`, body).then(r => r.data),

  convertLead: (id: string, tenantBody: {
    tenantName: string; tenantSlug: string;
    adminEmail: string; adminPassword: string;
    plan?: string; billingEmail?: string;
  }) =>
    platform.post<{ tenantId: string; tenantSlug: string; adminEmail: string }>(`/api/platform/leads/${id}/convert`, tenantBody).then(r => r.data),

  // ── Settings ──────────────────────────────────────────────────────────────

  getSettings: () =>
    platform.get<PlatformSettings>('/api/platform/settings').then(r => r.data),

  updateSmtpSettings: (body: { host: string; port: number; username: string; password?: string; fromEmail: string; fromName?: string; useSsl: boolean }) =>
    platform.put('/api/platform/settings/smtp', body).then(r => r.data),

  testSmtp: () =>
    platform.post<{ sent: boolean; message: string }>('/api/platform/settings/smtp/test').then(r => r.data),

  getVersion: () =>
    platform.get<{ version: string; environment: string; deployedAt?: string; migrations?: number }>('/api/platform/settings/version').then(r => r.data),

  // ── Billing Summary ───────────────────────────────────────────────────────

  getBillingSummary: () =>
    platform.get<BillingSummary>('/api/platform/billing/summary').then(r => r.data),

  listAllInvoices: (params?: { status?: string; page?: number; pageSize?: number }) =>
    platform.get<{ total: number; page: number; invoices: (TenantInvoice & { tenantName: string; tenantSlug: string })[] }>(
      '/api/platform/billing/invoices', { params }
    ).then(r => r.data),

  // ── Security Center ───────────────────────────────────────────────────────

  getSecuritySummary: () =>
    platform.get<TenantSecurityPosture[]>('/api/platform/security/summary').then(r => r.data),

  getTenantSecurityPolicy: (tenantId: string) =>
    platform.get<TenantSecurityPolicy>(`/api/platform/tenants/${tenantId}/security-policy`).then(r => r.data),

  updateTenantSecurityPolicy: (tenantId: string, body: Partial<TenantSecurityPolicy>) =>
    platform.put(`/api/platform/tenants/${tenantId}/security-policy`, body),

  getHealth: () =>
    platform.get<PlatformHealthStatus>('/api/platform/health').then(r => r.data),

  listTeam: () =>
    platform.get<PlatformTeamMember[]>('/api/platform/team').then(r => r.data),

  createTeamMember: (body: { email: string; fullName?: string; password: string; role?: string }) =>
    platform.post<PlatformTeamMember>('/api/platform/team', body).then(r => r.data),

  updateTeamMember: (id: string, body: { role?: string; isActive?: boolean; fullName?: string }) =>
    platform.patch<PlatformTeamMember>(`/api/platform/team/${id}`, body).then(r => r.data),

  deactivateTeamMember: (id: string) =>
    platform.patch(`/api/platform/team/${id}`, { isActive: false }).then(r => r.data),

  listInvoices: (tenantId: string) =>
    platform.get<TenantInvoice[]>(`/api/platform/tenants/${tenantId}/invoices`).then(r => r.data),

  createInvoice: (tenantId: string, body: {
    invoiceNumber?: string;   // optional — backend auto-generates INV-YYYY-NNNN when omitted
    amount: number;
    currencyCode?: string;
    status?: string;
    paymentMethod?: string;
    paymentReference?: string;
    periodDescription?: string;
    invoiceDate: string;
    dueDate: string;
    paidDate?: string | null;
    notes?: string;
    recipientEmail?: string;
  }) =>
    platform.post<TenantInvoice>(`/api/platform/tenants/${tenantId}/invoices`, body).then(r => r.data),

  updateInvoice: (tenantId: string, invoiceId: string, body: {
    status?: string;
    paymentMethod?: string;
    paymentReference?: string;
    paidDate?: string | null;
    notes?: string;
    recipientEmail?: string;
  }) =>
    platform.put<TenantInvoice>(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}`, body).then(r => r.data),

  deleteInvoice: (tenantId: string, invoiceId: string) =>
    platform.delete(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}`).then(r => r.data),

  getTenantAiUsage: (tenantId: string, yearMonth?: string) =>
    platform.get<TenantAiUsage>(`/api/platform/tenants/${tenantId}/ai-usage`, { params: yearMonth ? { yearMonth } : {} }).then(r => r.data),

  // ── Invoice Lines ──────────────────────────────────────────────────────────

  listInvoiceLines: (tenantId: string, invoiceId: string) =>
    platform.get<TenantInvoiceLine[]>(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/lines`).then(r => r.data),

  addInvoiceLine: (tenantId: string, invoiceId: string, body: {
    description: string; quantity: number; unitPrice: number;
    discountAmount?: number; taxRate?: number; sortOrder?: number;
  }) =>
    platform.post<TenantInvoiceLine>(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/lines`, body).then(r => r.data),

  updateInvoiceLine: (tenantId: string, invoiceId: string, lineId: string, body: {
    description?: string; quantity?: number; unitPrice?: number;
    discountAmount?: number; taxRate?: number; sortOrder?: number;
  }) =>
    platform.put<TenantInvoiceLine>(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/lines/${lineId}`, body).then(r => r.data),

  deleteInvoiceLine: (tenantId: string, invoiceId: string, lineId: string) =>
    platform.delete(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/lines/${lineId}`).then(r => r.data),

  // ── Payments ───────────────────────────────────────────────────────────────

  listInvoicePayments: (tenantId: string, invoiceId: string) =>
    platform.get<TenantPayment[]>(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/payments`).then(r => r.data),

  createPayment: (tenantId: string, invoiceId: string, body: {
    amount: number; currencyCode?: string; method: string;
    reference?: string; status?: string; paidAt?: string | null; notes?: string;
  }) =>
    platform.post<TenantPayment>(`/api/platform/tenants/${tenantId}/invoices/${invoiceId}/payments`, body).then(r => r.data),

  updatePayment: (tenantId: string, paymentId: string, body: {
    status?: string; amount?: number; reference?: string; paidAt?: string | null; notes?: string;
  }) =>
    platform.put<TenantPayment>(`/api/platform/tenants/${tenantId}/payments/${paymentId}`, body).then(r => r.data),

  deletePayment: (tenantId: string, paymentId: string) =>
    platform.delete(`/api/platform/tenants/${tenantId}/payments/${paymentId}`).then(r => r.data),

  // ── Login Activity ─────────────────────────────────────────────────────────

  listLoginActivity: (params?: {
    tenantId?: string; userId?: string; eventType?: string;
    from?: string; to?: string; page?: number; pageSize?: number;
  }) =>
    platform.get<{ total: number; page: number; pageSize: number; items: LoginActivity[] }>(
      '/api/platform/login-activity', { params }
    ).then(r => r.data),

  // ── Pricing Config ─────────────────────────────────────────────────────────

  getPricingConfig: () =>
    platform.get<PlatformPricingConfig[]>('/api/platform/pricing/config').then(r => r.data),

  updatePricingConfig: (key: string, value: number) =>
    platform.put<PlatformPricingConfig>(`/api/platform/pricing/config/${key}`, { value }).then(r => r.data),

  getPricingModules: () =>
    platform.get<PlatformPricingModule[]>('/api/platform/pricing/modules').then(r => r.data),

  updatePricingModule: (moduleKey: string, body: Partial<{
    includedInTrial: boolean; includedInStarter: boolean; includedInGrowth: boolean;
    includedInEnterprise: boolean; isEnterpriseOnly: boolean; addonPriceMonthly: number;
  }>) =>
    platform.put<PlatformPricingModule>(`/api/platform/pricing/modules/${moduleKey}`, body).then(r => r.data),

  // ── Quotes ────────────────────────────────────────────────────────────────

  listQuotes: (status?: string, page = 1) =>
    platform.get<{ total: number; page: number; items: PlatformQuote[] }>(
      '/api/platform/quotes', { params: { status, page } }
    ).then(r => r.data),

  getQuote: (id: string) =>
    platform.get<PlatformQuote>(`/api/platform/quotes/${id}`).then(r => r.data),

  patchQuote: (id: string, body: { status?: string; notes?: string }) =>
    platform.patch<PlatformQuote>(`/api/platform/quotes/${id}`, body).then(r => r.data),

  createQuote: (body: {
    companyName: string; contactEmail: string; contactName?: string;
    phone?: string; numEmployees?: number; estimatedMonthlyAmount?: number; notes?: string;
  }) =>
    platform.post<PlatformQuote>('/api/platform/quotes', body).then(r => r.data),

  convertQuote: (id: string, body: {
    slug: string; adminEmail: string; adminFullName?: string; adminPassword: string;
    plan?: string; maxUsers?: number; maxEmployees?: number; maxCompanies?: number;
    maxAdminUsers?: number; billingCycle?: string; expiresAtUtc?: string | null;
  }) =>
    platform.post<{ tenantId: string }>(`/api/platform/quotes/${id}/convert`, body).then(r => r.data),

  // ── Compliance ────────────────────────────────────────────────────────────────
  listComplianceControls: () =>
    platform.get<ComplianceControl[]>('/api/platform/compliance/controls').then(r => r.data),
  createComplianceControl: (body: ComplianceControlBody) =>
    platform.post<{ id: string }>('/api/platform/compliance/controls', body).then(r => r.data),
  updateComplianceControl: (id: string, body: Partial<ComplianceControlBody>) =>
    platform.patch(`/api/platform/compliance/controls/${id}`, body),
  listSecurityIncidents: () =>
    platform.get<SecurityIncident[]>('/api/platform/compliance/incidents').then(r => r.data),
  createSecurityIncident: (body: SecurityIncidentBody) =>
    platform.post<{ id: string }>('/api/platform/compliance/incidents', body).then(r => r.data),
  updateSecurityIncident: (id: string, body: Partial<SecurityIncidentBody>) =>
    platform.patch(`/api/platform/compliance/incidents/${id}`, body),
  getComplianceSummary: () =>
    platform.get<ComplianceSummary>('/api/platform/compliance/summary').then(r => r.data),

  // ── Settings Diagnostics & Maintenance ───────────────────────────────────────
  getDiagnostics: () =>
    platform.get<PlatformDiagnostics>('/api/platform/settings/diagnostics').then(r => r.data),
  setMaintenanceMode: (enabled: boolean, message?: string) =>
    platform.put('/api/platform/settings/maintenance', { enabled, message }).then(r => r.data),

  // ── Feature Flags Catalog ─────────────────────────────────────────────────
  getFeatureFlags: () =>
    platform.get<{ key: string; label: string; category: string }[]>('/api/platform/feature-flags').then(r => r.data),
};

// ── Compliance types ──────────────────────────────────────────────────────────

export interface ComplianceControl {
  id: string;
  category: string;
  controlId: string;
  title: string;
  description?: string;
  status: string;
  owner?: string;
  evidenceNote?: string;
  evidenceUrl?: string;
  reviewedAtUtc?: string;
  updatedAtUtc?: string;
  createdAtUtc: string;
}

export interface ComplianceControlBody {
  category: string;
  controlId: string;
  title: string;
  description?: string;
  status?: string;
  owner?: string;
  evidenceNote?: string;
  evidenceUrl?: string;
  reviewed?: boolean;
}

export interface SecurityIncident {
  id: string;
  title: string;
  description?: string;
  severity: string;
  status: string;
  reporter?: string;
  affectedSystems?: string;
  occurredAtUtc: string;
  resolvedAtUtc?: string;
  resolution?: string;
  createdAtUtc: string;
}

export interface SecurityIncidentBody {
  title?: string;
  description?: string;
  severity?: string;
  status?: string;
  reporter?: string;
  affectedSystems?: string;
  resolution?: string;
  occurredAtUtc?: string;
}

export interface ComplianceSummary {
  totalControls: number;
  implemented: number;
  inProgress: number;
  notStarted: number;
  waived: number;
  implementationPct: number;
  openIncidents: number;
  criticalIncidents: number;
}

export interface PlatformDiagnostics {
  tenantCount: number;
  activeTenants: number;
  employeeCount: number;
  databaseOk: boolean;
  aiProvider: string;
  aiConfigured: boolean;
  maintenance: boolean;
  maintenanceMsg: string;
  serverTimeUtc: string;
}
