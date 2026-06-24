import client from './client';

export interface PagedResult<T> {
  items: T[];
  total: number;
  page: number;
  pageSize: number;
}

// Shape returned by master-data import endpoints backed by ImportCommitResult
// (companies, branches, departments, designations, grades, cost-centres, locations).
// Per-row failures live in `rows`, batch-level failures in `globalErrors` — there is
// no flat `errors` array, so the toolbar must be fed a normalised ImportResult.
export interface RawImportCommit {
  received: number;
  created: number;
  updated: number;
  skipped: number;
  rows?: { rowNumber: number; entityCode?: string | null; status: string; errors?: string[] }[];
  globalErrors?: string[];
}

export function toImportResult(d: RawImportCommit): { received: number; created: number; skipped: number; errors: string[] } {
  const rowErrors = (d.rows ?? [])
    .filter((r) => (r.errors?.length ?? 0) > 0)
    .map((r) => `Row ${r.rowNumber}${r.entityCode ? ` (${r.entityCode})` : ''}: ${(r.errors ?? []).join(', ')}`);
  return {
    received: d.received ?? 0,
    created: d.created ?? 0,
    skipped: d.skipped ?? 0,
    errors: [...(d.globalErrors ?? []), ...rowErrors],
  };
}

// ─── Company ────────────────────────────────────────────────────────────────

export interface CompanyDto {
  id: string;
  legalNameEn: string;
  legalNameAr: string;
  tradeName: string;
  countryCode: string;
  jurisdiction: string;
  registrationNumber: string;
  taxNumber: string;
  wpsEmployerId: string;
  gosiEmployerId: string;
  qiwaEstablishmentId: string;
  defaultCurrency: string;
  isActive: boolean;
}

export interface CompanyRequest {
  legalNameEn: string;
  legalNameAr?: string;
  tradeName?: string;
  countryCode: string;
  jurisdiction?: string;
  registrationNumber: string;
  taxNumber?: string;
  wpsEmployerId?: string;
  gosiEmployerId?: string;
  qiwaEstablishmentId?: string;
  defaultCurrency: string;
  isActive: boolean;
}

export const companiesApi = {
  list: (page = 1, pageSize = 25) =>
    client.get<PagedResult<CompanyDto>>('/api/companies', { params: { page, pageSize } }).then((r) => r.data),
  get: (id: string) =>
    client.get<CompanyDto>(`/api/companies/${id}`).then((r) => r.data),
  create: (data: CompanyRequest) =>
    client.post<CompanyDto>('/api/companies', data).then((r) => r.data),
  update: (id: string, data: CompanyRequest) =>
    client.put<CompanyDto>(`/api/companies/${id}`, data).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/companies/${id}`),
  export: () => client.get<string>('/api/companies/export', { responseType: 'text' }).then((r) => r.data),
  importTemplate: () => client.get<string>('/api/companies/import-template', { responseType: 'text' }).then((r) => r.data),
  import: (csv: string) => client.post<RawImportCommit>('/api/companies/import', { csv }).then((r) => toImportResult(r.data)),
};

// ─── Branch ─────────────────────────────────────────────────────────────────

export interface BranchDto {
  id: string;
  companyId: string;
  code: string;
  nameEn: string;
  nameAr: string;
  countryCode: string;
  city: string;
  addressLine1: string;
  addressLine2: string;
  timeZoneId: string;
  laborOfficeCode: string;
  isHeadOffice: boolean;
  isActive: boolean;
}

export interface BranchRequest {
  companyId: string;
  code: string;
  nameEn: string;
  nameAr?: string;
  countryCode: string;
  city: string;
  addressLine1?: string;
  addressLine2?: string;
  timeZoneId: string;
  laborOfficeCode?: string;
  isHeadOffice: boolean;
  isActive: boolean;
}

export const branchesApi = {
  list: (companyId?: string, page = 1, pageSize = 25) =>
    client.get<PagedResult<BranchDto>>('/api/branches', { params: { companyId, page, pageSize } }).then((r) => r.data),
  get: (id: string) =>
    client.get<BranchDto>(`/api/branches/${id}`).then((r) => r.data),
  create: (data: BranchRequest) =>
    client.post<BranchDto>('/api/branches', data).then((r) => r.data),
  update: (id: string, data: BranchRequest) =>
    client.put<BranchDto>(`/api/branches/${id}`, data).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/branches/${id}`),
  export: () => client.get<string>('/api/branches/export', { responseType: 'text' }).then((r) => r.data),
  importTemplate: () => client.get<string>('/api/branches/import-template', { responseType: 'text' }).then((r) => r.data),
  import: (csv: string) => client.post<RawImportCommit>('/api/branches/import', { csv }).then((r) => toImportResult(r.data)),
};

// ─── Department ──────────────────────────────────────────────────────────────

export interface DepartmentDto {
  id: string;
  branchId?: string;
  parentDepartmentId?: string;
  costCenterId?: string;
  code: string;
  nameEn: string;
  nameAr: string;
  managerEmployeeId?: number;
  isActive: boolean;
}

export interface DepartmentRequest {
  branchId?: string;
  parentDepartmentId?: string;
  costCenterId?: string;
  code: string;
  nameEn: string;
  nameAr?: string;
  managerEmployeeId?: number;
  isActive: boolean;
}

export const departmentsApi = {
  list: (branchId?: string, page = 1, pageSize = 25) =>
    client.get<PagedResult<DepartmentDto>>('/api/departments', { params: { branchId, page, pageSize } }).then((r) => r.data),
  get: (id: string) =>
    client.get<DepartmentDto>(`/api/departments/${id}`).then((r) => r.data),
  create: (data: DepartmentRequest) =>
    client.post<DepartmentDto>('/api/departments', data).then((r) => r.data),
  update: (id: string, data: DepartmentRequest) =>
    client.put<DepartmentDto>(`/api/departments/${id}`, data).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/departments/${id}`),
  export: () => client.get<string>('/api/departments/export', { responseType: 'text' }).then((r) => r.data),
  importTemplate: () => client.get<string>('/api/departments/import-template', { responseType: 'text' }).then((r) => r.data),
  import: (csv: string) => client.post<RawImportCommit>('/api/departments/import', { csv }).then((r) => toImportResult(r.data)),
};

// ─── Designation ─────────────────────────────────────────────────────────────

export interface DesignationDto {
  id: string;
  departmentId?: string;
  code: string;
  titleEn: string;
  titleAr: string;
  jobGrade: string;
  gradeId?: string;
  jobLevel: string;
  jobDescription: string;
  isManagerRole: boolean;
  isActive: boolean;
}

export interface DesignationRequest {
  departmentId?: string;
  code: string;
  titleEn: string;
  titleAr?: string;
  jobGrade?: string;
  gradeId?: string;
  jobLevel?: string;
  jobDescription?: string;
  isManagerRole: boolean;
  isActive: boolean;
}

export const designationsApi = {
  list: (departmentId?: string, page = 1, pageSize = 25) =>
    client.get<PagedResult<DesignationDto>>('/api/designations', { params: { departmentId, page, pageSize } }).then((r) => r.data),
  get: (id: string) =>
    client.get<DesignationDto>(`/api/designations/${id}`).then((r) => r.data),
  create: (data: DesignationRequest) =>
    client.post<DesignationDto>('/api/designations', data).then((r) => r.data),
  update: (id: string, data: DesignationRequest) =>
    client.put<DesignationDto>(`/api/designations/${id}`, data).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/designations/${id}`),
  export: () => client.get<string>('/api/designations/export', { responseType: 'text' }).then((r) => r.data),
  importTemplate: () => client.get<string>('/api/designations/import-template', { responseType: 'text' }).then((r) => r.data),
  import: (csv: string) => client.post<RawImportCommit>('/api/designations/import', { csv }).then((r) => toImportResult(r.data)),
};

// ─── Grade ──────────────────────────────────────────────────────────────────

export interface GradeDto {
  id: string;
  code: string;
  name: string;
  band: string;
  level: number;
  isActive: boolean;
}

export interface GradeRequest {
  code: string;
  name: string;
  band?: string;
  level: number;
  isActive: boolean;
}

export const gradesApi = {
  list: (page = 1, pageSize = 100) =>
    client.get<PagedResult<GradeDto>>('/api/grades', { params: { page, pageSize } }).then((r) => r.data),
  get: (id: string) =>
    client.get<GradeDto>(`/api/grades/${id}`).then((r) => r.data),
  create: (data: GradeRequest) =>
    client.post<GradeDto>('/api/grades', data).then((r) => r.data),
  update: (id: string, data: GradeRequest) =>
    client.put<GradeDto>(`/api/grades/${id}`, data).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/grades/${id}`),
  export: () => client.get<string>('/api/grades/export', { responseType: 'text' }).then((r) => r.data),
  importTemplate: () => client.get<string>('/api/grades/import-template', { responseType: 'text' }).then((r) => r.data),
  import: (csv: string) => client.post<RawImportCommit>('/api/grades/import', { csv }).then((r) => toImportResult(r.data)),
};

// ─── Cost Center ────────────────────────────────────────────────────────────

export interface CostCenterDto {
  id: string;
  companyId?: string;
  code: string;
  name: string;
  isActive: boolean;
}

export interface CostCenterRequest {
  companyId?: string;
  code: string;
  name: string;
  isActive: boolean;
}

export const costCentersApi = {
  list: (companyId?: string, page = 1, pageSize = 100) =>
    client.get<PagedResult<CostCenterDto>>('/api/organization/cost-centers', { params: { companyId, page, pageSize } }).then((r) => r.data),
  get: (id: string) =>
    client.get<CostCenterDto>(`/api/cost-centers/${id}`).then((r) => r.data),
  create: (data: CostCenterRequest) =>
    client.post<CostCenterDto>('/api/organization/cost-centers', data).then((r) => r.data),
  update: (id: string, data: CostCenterRequest) =>
    client.put<CostCenterDto>(`/api/cost-centers/${id}`, data).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/cost-centers/${id}`),
  export: () => client.get<string>('/api/cost-centers/export', { responseType: 'text' }).then((r) => r.data),
  importTemplate: () => client.get<string>('/api/cost-centers/import-template', { responseType: 'text' }).then((r) => r.data),
  import: (csv: string) => client.post<RawImportCommit>('/api/cost-centers/import', { csv }).then((r) => toImportResult(r.data)),
};
