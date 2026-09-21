import client from './client';

// ── Types ─────────────────────────────────────────────────────────────────────

export interface AIQueryResponse {
  answer: string;
  intent: string;
  wasBlocked: boolean;
  blockedReason: string;
  tokensUsed: number;
  isAdvisory: boolean;
  suggestions: string[];
}

export interface AIInsight {
  id: string;
  tenantId: string;
  module: string;
  insightType: string;
  severity: 'Info' | 'Warning' | 'Critical';
  employeeId?: number;
  employeeName: string;
  title: string;
  summary: string;
  dataJson: string;
  generatedBy: string;
  isAcknowledged: boolean;
  acknowledgedBy?: string;
  acknowledgedAtUtc?: string;
  createdAtUtc: string;
}

export interface EmployeeRiskScore {
  id: string;
  tenantId: string;
  employeeId: number;
  employeeName: string;
  departmentName: string;
  churnRiskScore: number;
  burnoutRiskScore: number;
  performanceDeclineScore: number;
  overallRiskLevel: 'Low' | 'Medium' | 'High' | 'Critical';
  riskFactorsJson: string;
  recommendations: string;
  isAdvisoryOnly: boolean;
  computedAtUtc: string;
  acknowledgedAtUtc?: string;
}

export interface AIHRQueryLog {
  id: string;
  userId: string;
  employeeId?: number;
  userRole: string;
  query: string;
  response: string;
  intentClassified: string;
  wasBlocked: boolean;
  blockedReason: string;
  tokensUsed: number;
  responseTimeMs: number;
  isAdvisoryLabelShown: boolean;
  createdAtUtc: string;
}

export interface PayrollAIValidationResult {
  id: string;
  payrollRunId: string;
  employeeId?: number;
  employeeName: string;
  validationType: string;
  severity: 'Info' | 'Warning' | 'Critical';
  message: string;
  dataJson: string;
  isResolved: boolean;
  isAdvisoryOnly: boolean;
  createdAtUtc: string;
}

export interface HRRequest {
  id: string;
  tenantId: string;
  employeeId: number;
  categoryId?: string;
  categoryName: string;
  subject: string;
  description: string;
  priority: string;
  status: string;
  dueAtUtc: string;
  createdAtUtc: string;
}

export interface HRRequestComment {
  id: string;
  hrRequestId: string;
  employeeId: number;
  comment: string;
  createdAtUtc: string;
}

export interface HRRequestCategory {
  id: string;
  name: string;
  code: string;
  defaultSlaHours: number;
  isActive: boolean;
}

export interface TenantFeatureFlag {
  id: string;
  featureKey: string;
  isEnabled: boolean;
  configJson?: string;
  updatedAtUtc: string;
}

export interface TenantLocalizationSetting {
  id?: string;
  tenantId?: string;
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
}

export interface TenantBranding {
  id?: string;
  logoUrl: string;
  primaryColor: string;
  accentColor: string;
  companyNameEn: string;
  companyNameAr: string;
  portalTitle: string;
  faviconUrl: string;
}

export interface TenantSubscription {
  id?: string;
  plan: string;
  status: string;
  maxEmployees: number;
  maxUsers: number;
  billingEmail: string;
  billingCycle: string;
  monthlyAmount: number;
  currencyCode: string;
  startedAtUtc: string;
  expiresAtUtc?: string;
}

export interface TenantInvoiceSummary {
  id: string;
  invoiceNumber: string;
  amount: number;
  currencyCode: string;
  status: 'Draft' | 'Sent' | 'Paid' | 'Overdue' | 'Cancelled';
  paymentMethod: string | null;
  periodDescription: string | null;
  invoiceDate: string;
  dueDate: string;
  paidDate: string | null;
  createdAtUtc: string;
}

export interface TenantAiUsageSummary {
  plan: string;
  yearMonth: number;
  tokensUsed: number;
  requestCount: number;
  blockedCount: number;
  monthlyTokenLimit: number;
  isUnlimited: boolean;
  usagePct: number;
}

// ── AI Assistant API ──────────────────────────────────────────────────────────

export interface AIProviderStatus {
  enabled: boolean;
  provider: string;
}

export const aiAssistantApi = {
  status: () =>
    client.get<AIProviderStatus>('/api/ai/status').then(r => r.data),

  query: (query: string, employeeId?: number) =>
    client.post<AIQueryResponse>('/api/ai/query', { query, employeeId }).then(r => r.data),

  listInsights: (params?: { module?: string; severity?: string; acknowledged?: boolean; page?: number; pageSize?: number }) =>
    client.get<{ items: AIInsight[]; total: number; page: number; pageSize: number }>(
      '/api/ai/insights', { params }
    ).then(r => r.data),

  acknowledgeInsight: (id: string) =>
    client.post<AIInsight>(`/api/ai/insights/${id}/acknowledge`).then(r => r.data),

  listRiskScores: (params?: { riskLevel?: string; department?: string }) =>
    client.get<EmployeeRiskScore[]>('/api/ai/risk-scores', { params }).then(r => r.data),

  computeRiskScores: () =>
    client.post<{ computed: number; message: string }>('/api/ai/risk-scores/compute').then(r => r.data),

  queryHistory: (params?: { page?: number; pageSize?: number }) =>
    client.get<{ items: AIHRQueryLog[]; total: number; page: number }>('/api/ai/query-history', { params }).then(r => r.data),

  getPayrollValidation: (payrollRunId: string) =>
    client.get<PayrollAIValidationResult[]>(`/api/ai/payroll-validation/${payrollRunId}`).then(r => r.data),

  runPayrollValidation: (payrollRunId: string) =>
    client.post<{ findings: number; critical: number; warnings: number; message: string; results: PayrollAIValidationResult[] }>(
      `/api/ai/payroll-validation/${payrollRunId}/run`
    ).then(r => r.data),
};

// ── HR Request Center API ─────────────────────────────────────────────────────

export const hrRequestApi = {
  listCategories: () =>
    client.get<HRRequestCategory[]>('/api/hr-requests/categories').then(r => r.data),

  createCategory: (body: { name: string; code: string; defaultSlaHours?: number }) =>
    client.post<HRRequestCategory>('/api/hr-requests/categories', body).then(r => r.data),

  list: (params?: { employeeId?: number; status?: string; priority?: string; page?: number; pageSize?: number }) =>
    client.get<{ items: HRRequest[]; total: number; page: number; pageSize: number }>(
      '/api/hr-requests', { params }
    ).then(r => r.data),

  get: (id: string) =>
    client.get<{ request: HRRequest; comments: HRRequestComment[]; attachments: unknown[] }>(
      `/api/hr-requests/${id}`
    ).then(r => r.data),

  create: (body: {
    employeeId: number;
    categoryId?: string;
    categoryName?: string;
    subject: string;
    description: string;
    priority?: string;
  }) => client.post<HRRequest>('/api/hr-requests', body).then(r => r.data),

  updateStatus: (id: string, status: string) =>
    client.patch<HRRequest>(`/api/hr-requests/${id}/status`, { status }).then(r => r.data),

  addComment: (id: string, employeeId: number, comment: string) =>
    client.post<HRRequestComment>(`/api/hr-requests/${id}/comments`, { employeeId, comment }).then(r => r.data),

  dashboard: () =>
    client.get<{ open: number; inProgress: number; resolved: number; overdue: number; recentRequests: HRRequest[] }>(
      '/api/hr-requests/dashboard'
    ).then(r => r.data),
};

// ── Tenant Admin API ──────────────────────────────────────────────────────────

export const tenantAdminApi = {
  getSubscription: () =>
    client.get<TenantSubscription | null>('/api/tenant-admin/subscription').then(r => r.data),

  listFeatureFlags: () =>
    client.get<TenantFeatureFlag[]>('/api/tenant-admin/feature-flags').then(r => r.data),

  setFeatureFlag: (featureKey: string, isEnabled: boolean, configJson?: string) =>
    client.put<TenantFeatureFlag>(`/api/tenant-admin/feature-flags/${featureKey}`, { isEnabled, configJson }).then(r => r.data),

  getLocalization: () =>
    client.get<TenantLocalizationSetting>('/api/tenant-admin/localization').then(r => r.data),

  upsertLocalization: (body: TenantLocalizationSetting) =>
    client.put<TenantLocalizationSetting>('/api/tenant-admin/localization', body).then(r => r.data),

  getBranding: () =>
    client.get<TenantBranding | null>('/api/tenant-admin/branding').then(r => r.data),

  upsertBranding: (body: Partial<TenantBranding>) =>
    client.put<TenantBranding>('/api/tenant-admin/branding', body).then(r => r.data),

  listInvoices: () =>
    client.get<TenantInvoiceSummary[]>('/api/tenant-admin/invoices').then(r => r.data),

  getAiUsage: (yearMonth?: number) =>
    client.get<TenantAiUsageSummary>('/api/tenant-admin/ai-usage', { params: yearMonth ? { yearMonth } : {} }).then(r => r.data),
};

// ── Features API (accessible to all authenticated tenant users) ───────────────

/** A module as the tenant administrator sees it, including why it may be locked. */
export interface TenantModule {
  key: string;
  labelEn: string;
  labelAr: string;
  description: string;
  enabled: boolean;
  /** False when this module is load-bearing or statutory for this tenant. */
  canDisable: boolean;
  /** "Optional" | "Core" | "Statutory" */
  lockClass: string;
  /** Present only when canDisable is false. Shown to the administrator verbatim. */
  lockReason: string | null;
}

export interface TenantModuleList {
  countryCode: string | null;
  modules: TenantModule[];
}

export const tenantModulesApi = {
  list: () =>
    client.get<TenantModuleList>('/api/tenant-modules').then(r => r.data),

  /**
   * Switch a module on or off. Rejects with 409 `module_not_disableable` for a load-bearing or
   * statutory module, 501 `module_not_enforceable` for a key nothing reads, and 400
   * `unknown_module` otherwise — deliberately, rather than storing a flag that changes nothing.
   */
  set: (moduleKey: string, enabled: boolean) =>
    client.put<TenantModule>(`/api/tenant-modules/${moduleKey}`, { enabled }).then(r => r.data),
};

/** One module and the frontend routes it owns, as the backend catalog defines them. */
export interface ModuleNav {
  key: string;
  labelEn: string;
  navPaths: string[];
  enabled: boolean;
}

export const featuresApi = {
  /**
   * Returns the module keys that are EFFECTIVELY disabled for the tenant — the stored flags after
   * the backend has applied its statutory and core locks. An absent key means enabled.
   */
  getDisabledKeys: () =>
    client.get<string[]>('/api/features/disabled-keys').then(r => r.data),

  /**
   * Which routes belong to which module, and whether that module is on.
   *
   * The mapping is served rather than hard-coded here so the navigation, the route guard and the
   * API guard cannot drift apart — they all resolve against the same backend catalog.
   */
  getModules: () =>
    client.get<ModuleNav[]>('/api/features/modules').then(r => r.data),
};
