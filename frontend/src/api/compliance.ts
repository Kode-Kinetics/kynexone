import client from './client';
import { fetchAllPages } from '../lib/paging';

// ── Types ─────────────────────────────────────────────────────────────────────

export interface DocType {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  category: string;
  expiryRequired: boolean;
  alertDaysBeforeExpiry: number;
  isMandatory: boolean;
  applicableCountries: string;
  isActive: boolean;
}

export interface ContractTemplate {
  id: string;
  code: string;
  nameEn: string;
  nameAr: string;
  contractType: string;
  language: string;
  contentHtmlEn: string;
  contentHtmlAr: string;
  variables: string;
  countryCode: string;
  isActive: boolean;
  version: number;
  createdAtUtc: string;
}

export interface EmployeeContract {
  id: string;
  employeeId: string;
  employeeName: string;
  templateId: string | null;
  contractNumber: string;
  contractType: string;
  status: string;
  startDate: string;
  endDate: string | null;
  basicSalary: number;
  currencyCode: string;
  language: string;
  version: number;
  previousVersionId: string | null;
  signedByEmployeeName: string;
  signedByEmployeeAtUtc: string | null;
  signedByHrName: string;
  signedByHrAtUtc: string | null;
  fileUrl: string;
  createdAtUtc: string;
  updatedAtUtc: string | null;
}

export interface VisaRecord {
  id: string;
  employeeId: string;
  employeeName: string;
  visaType: string;
  visaNumber: string;
  iqamaNumber: string;
  emiratesIdNumber: string;
  countryCode: string;
  issueDate: string;
  expiryDate: string;
  sponsor: string;
  status: string;
  fileUrl: string;
  createdAtUtc: string;
}

export interface PassportRecord {
  id: string;
  employeeId: string;
  employeeName: string;
  passportNumber: string;
  nationality: string;
  issuingCountry: string;
  dateOfBirth: string;
  issueDate: string;
  expiryDate: string;
  placeOfIssue: string;
  isHeldByCompany: boolean;
  returnedToEmployeeDate: string | null;
  status: string;
  fileUrl: string;
  createdAtUtc: string;
}

export interface WorkPermitRecord {
  id: string;
  employeeId: string;
  employeeName: string;
  permitNumber: string;
  countryCode: string;
  permitType: string;
  issueDate: string;
  expiryDate: string;
  issuingAuthority: string;
  status: string;
  fileUrl: string;
  createdAtUtc: string;
}

export interface ComplianceRenewal {
  id: string;
  employeeId: string;
  employeeName: string;
  documentType: string;
  documentNumber: string;
  expiryDate: string;
  renewalDate: string | null;
  status: string;
  assignedToName: string;
  notes: string;
  createdAtUtc: string;
}

export interface ComplianceAIInsight {
  type: string;
  severity: string;
  title: string;
  description: string;
  isAdvisory: boolean;
}

export interface ComplianceDashboard {
  activeContracts: number;
  visasExpiring30: number;
  visasExpiring60: number;
  passportsExpiring90: number;
  expiredVisas: number;
  pendingRenewals: number;
  passportsHeldByCompany: number;
}

export interface ExpiryAlert {
  employeeId: string;
  employeeName: string;
  type: string;
  subType: string;
  expiryDate: string;
  daysLeft: number;
}

// ── API Clients ───────────────────────────────────────────────────────────────

export const complianceDocTypesApi = {
  list: (activeOnly = true) =>
    client.get<DocType[]>('/api/compliance/doc-types', { params: { activeOnly } }).then(r => r.data),

  create: (body: { code: string; nameEn: string; nameAr?: string; category: string; expiryRequired: boolean; alertDaysBeforeExpiry: number; isMandatory: boolean; applicableCountries?: string }) =>
    client.post<DocType>('/api/compliance/doc-types', body).then(r => r.data),
};

export const complianceContractsApi = {
  listTemplates: (activeOnly = true) =>
    client.get<ContractTemplate[]>('/api/compliance/contracts/templates', { params: { activeOnly } }).then(r => r.data),

  getTemplate: (id: string) =>
    client.get<ContractTemplate>(`/api/compliance/contracts/templates/${id}`).then(r => r.data),

  createTemplate: (body: { code: string; nameEn: string; nameAr?: string; contractType: string; language?: string; contentHtmlEn?: string; contentHtmlAr?: string; variables?: string; countryCode?: string }) =>
    client.post<ContractTemplate>('/api/compliance/contracts/templates', body).then(r => r.data),

  list: (params: { employeeId?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; items: EmployeeContract[] }>('/api/compliance/contracts', { params }).then(r => r.data),

  /** Every matching contract, page by page: the register is acted on row by row (activate, expire). */
  listAll: (params: { employeeId?: string; status?: string } = {}) =>
    fetchAllPages((page, pageSize) => complianceContractsApi.list({ ...params, page, pageSize })),

  get: (id: string) =>
    client.get<EmployeeContract>(`/api/compliance/contracts/${id}`).then(r => r.data),

  create: (body: { employeeId: string; templateId?: string; contractType?: string; startDate: string; endDate?: string; basicSalary: number; currencyCode?: string; contentHtmlEn?: string; contentHtmlAr?: string; language?: string }) =>
    client.post<EmployeeContract>('/api/compliance/contracts', body).then(r => r.data),

  updateStatus: (id: string, status: string, signedByHrName?: string) =>
    client.patch<EmployeeContract>(`/api/compliance/contracts/${id}/status`, { status, signedByHrName }).then(r => r.data),

  supersede: (id: string, body: { employeeId: string; startDate: string; basicSalary: number; endDate?: string; currencyCode?: string }) =>
    client.post<EmployeeContract>(`/api/compliance/contracts/${id}/supersede`, body).then(r => r.data),
};

export const complianceVisaApi = {
  list: (params: { employeeId?: string; status?: string; countryCode?: string; expiringInDays?: number; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; items: VisaRecord[] }>('/api/compliance/visa-tracking', { params }).then(r => r.data),

  /** Every matching visa / iqama record, soonest expiry first, page by page (the server default page is 20). */
  listAll: (params: { employeeId?: string; status?: string; countryCode?: string; expiringInDays?: number } = {}) =>
    fetchAllPages((page, pageSize) => complianceVisaApi.list({ ...params, page, pageSize })),

  get: (id: string) =>
    client.get<VisaRecord>(`/api/compliance/visa-tracking/${id}`).then(r => r.data),

  create: (body: { employeeId: string; visaType: string; visaNumber?: string; iqamaNumber?: string; emiratesIdNumber?: string; countryCode: string; issueDate: string; expiryDate: string; sponsor?: string; fileUrl?: string }) =>
    client.post<VisaRecord>('/api/compliance/visa-tracking', body).then(r => r.data),

  update: (id: string, body: Partial<VisaRecord>) =>
    client.put<VisaRecord>(`/api/compliance/visa-tracking/${id}`, body).then(r => r.data),
};

export const compliancePassportsApi = {
  list: (params: { employeeId?: string; status?: string; expiringInDays?: number; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; items: PassportRecord[] }>('/api/compliance/passports', { params }).then(r => r.data),

  /** Every matching passport, soonest expiry first, page by page. */
  listAll: (params: { employeeId?: string; status?: string; expiringInDays?: number } = {}) =>
    fetchAllPages((page, pageSize) => compliancePassportsApi.list({ ...params, page, pageSize })),

  create: (body: { employeeId: string; passportNumber: string; nationality?: string; issuingCountry?: string; dateOfBirth: string; issueDate: string; expiryDate: string; placeOfIssue?: string; isHeldByCompany?: boolean; fileUrl?: string }) =>
    client.post<PassportRecord>('/api/compliance/passports', body).then(r => r.data),
};

export const complianceWorkPermitsApi = {
  list: (params: { employeeId?: string; status?: string; expiringInDays?: number; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; items: WorkPermitRecord[] }>('/api/compliance/work-permits', { params }).then(r => r.data),

  /** Every matching work permit, soonest expiry first, page by page. */
  listAll: (params: { employeeId?: string; status?: string; expiringInDays?: number } = {}) =>
    fetchAllPages((page, pageSize) => complianceWorkPermitsApi.list({ ...params, page, pageSize })),

  create: (body: { employeeId: string; permitNumber: string; countryCode: string; permitType: string; issueDate: string; expiryDate: string; issuingAuthority?: string; fileUrl?: string }) =>
    client.post<WorkPermitRecord>('/api/compliance/work-permits', body).then(r => r.data),
};

export const complianceRenewalsApi = {
  list: (params: { employeeId?: string; status?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; page: number; items: ComplianceRenewal[] }>('/api/compliance/renewals', { params }).then(r => r.data),

  /** Every matching renewal, soonest expiry first, page by page: each one is a task someone acts on. */
  listAll: (params: { employeeId?: string; status?: string } = {}) =>
    fetchAllPages((page, pageSize) => complianceRenewalsApi.list({ ...params, page, pageSize })),

  create: (body: { employeeId: string; documentType: string; documentNumber?: string; expiryDate: string; assignedToName?: string; notes?: string }) =>
    client.post<ComplianceRenewal>('/api/compliance/renewals', body).then(r => r.data),

  updateStatus: (id: string, status: string, renewalDate?: string, notes?: string) =>
    client.patch<ComplianceRenewal>(`/api/compliance/renewals/${id}/status`, { status, renewalDate, notes }).then(r => r.data),
};

export const complianceReportsApi = {
  dashboard: () =>
    client.get<ComplianceDashboard>('/api/compliance/reports/dashboard').then(r => r.data),

  expiryAlerts: (withinDays = 90) =>
    client.get<{ withinDays: number; total: number; alerts: ExpiryAlert[] }>('/api/compliance/reports/expiry-alerts', { params: { withinDays } }).then(r => r.data),

  aiInsights: () =>
    client.get<{ generatedAt: string; isAdvisory: boolean; insights: ComplianceAIInsight[] }>('/api/compliance/reports/ai-insights').then(r => r.data),

  requirements: (countryCode?: string) =>
    client.get('/api/compliance/requirements', { params: { countryCode } }).then(r => r.data),
};
