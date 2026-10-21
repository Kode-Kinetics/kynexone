import client from './client';

/**
 * Create-company wizard. TS mirror of backend Application/CompanySetup/CompanySetupContracts.cs —
 * the shapes are fixed by docs/design/company-setup-wizard.md; change them there first.
 *
 * Deterministic only: nothing here reaches a model. Defaults come from the country pack.
 */

export interface CodeName { code: string; nameEn: string; nameAr?: string | null }
export interface GradeRef extends CodeName { currency: string }

export interface CompanySetupContext {
  canCreate: boolean;
  refusalCode?: string | null;
  refusalMessage?: string | null;
  creationMode: string;
  accountType: string;
  companiesUsed: number;
  companiesAllowed?: number | null;
  isGroupScope: boolean;
  sisterCompanies: { id: string; legalNameEn: string; countryCode: string }[];
  sharedCatalog: { departments: CodeName[]; designations: CodeName[]; grades: GradeRef[] };
  gradesCurrency?: string | null;
}

export type StartFrom = 'defaults' | 'copy';
export type CompanyAdminMode = 'invite' | 'existing' | 'groupOnly';

export interface CompanySetupCompany {
  legalNameEn: string;
  legalNameAr?: string;
  tradeName?: string;
  countryCode: string;
  currency: string;
  registrationNumber: string;
  molEstablishmentNumber?: string;
  gosiEmployerId?: string;
  unifiedNationalNumber?: string;
  taxNumber?: string;
  qiwaEstablishmentId?: string;
  emailDomain?: string;
  workEmailPattern?: string;
  erpCompanyCode?: string;
  /** "YYYY-MM" */
  firstPayrollMonth: string;
}

export interface CompanySetupLocation {
  code: string; nameEn: string; nameAr?: string;
  latitude?: number | null; longitude?: number | null; radiusMeters?: number | null;
}

export interface CompanySetupBranch {
  code: string; nameEn: string; nameAr?: string; city: string; timeZoneId: string; isHeadOffice: boolean;
  locations: CompanySetupLocation[];
}

export interface CompanySetupCostCentre { code: string; nameEn: string; nameAr?: string }
export interface CompanySetupDepartment { code: string; nameEn: string; nameAr?: string; parentCode?: string; branchCode?: string; costCentreCode?: string }
export interface CompanySetupDesignation { code: string; titleEn: string; titleAr?: string; departmentCode?: string; gradeCode?: string }
export interface CompanySetupGrade { code: string; nameEn: string; nameAr?: string; level: number; minSalary: number; midSalary: number; maxSalary: number }

export interface EmployeeNumbering {
  prefix: string; useCountryPrefix: boolean; useBranchPrefix: boolean; useDepartmentPrefix: boolean;
  useYear: boolean; paddingLength: number; nextSequence: number;
}

export interface CompanySetupRules {
  workWeek: { weekendDays: string; override: boolean };
  holidayCalendar: { usePack: boolean };
  leavePolicies: { usePack: boolean };
  employeeNumbering: EmployeeNumbering;
}

export interface CompanySetupAdmin {
  mode: CompanyAdminMode;
  existingUserId?: string;
  fullNameEn?: string;
  fullNameAr?: string;
  email?: string;
}

export interface CompanySetupDraftBody {
  startFrom: StartFrom;
  copyFromCompanyId?: string;
  company: CompanySetupCompany;
  branches: CompanySetupBranch[];
  costCentres: CompanySetupCostCentre[];
  departments: CompanySetupDepartment[];
  designations: CompanySetupDesignation[];
  grades: CompanySetupGrade[];
  rules: CompanySetupRules;
  companyAdmin: CompanySetupAdmin;
}

export type CompanySetupDraftStatus = 'Open' | 'Submitted' | 'Discarded';

export interface CompanySetupDraft {
  id: string;
  version: number;
  /** 1-7 */
  step: number;
  status: CompanySetupDraftStatus;
  body: CompanySetupDraftBody;
  createdByUserId: string;
  createdByName: string;
  createdAtUtc: string;
  updatedAtUtc: string;
  resultCompanyId?: string | null;
}

export interface CompanySetupDraftSummary { id: string; legalNameEn: string; step: number; updatedAtUtc: string; createdByName: string }

export type PlanArea =
  | 'company' | 'branch' | 'location' | 'costCentre' | 'department' | 'designation' | 'grade'
  | 'employeeIdRule' | 'cutover' | 'holidayCalendar' | 'leavePolicy' | 'gccSetting' | 'workWeek' | 'companyAdmin';
export type PlanAction = 'create' | 'reuse' | 'conflict';

export interface PlanItem { area: PlanArea; code: string; label: string; action: PlanAction; reason?: string | null }
export interface Problem { step: number; field: string; code: string; message: string; messageAr?: string | null }
export interface CompanySetupPlan { ok: boolean; items: PlanItem[]; problems: Problem[]; warnings: Problem[] }

export interface ReadinessItem {
  key: string;
  label: string;
  labelAr?: string | null;
  status: 'done' | 'todo' | 'recommended';
  scope: 'company' | 'workspace';
  href: string;
}

export interface CompanySetupResult {
  companyId: string;
  created: Partial<Record<PlanArea, number>>;
  reused: Partial<Record<PlanArea, number>>;
  readiness: ReadinessItem[];
  replayed: boolean;
}

const BASE = '/api/company-setup';

export const companySetupApi = {
  context: () => client.get<CompanySetupContext>(`${BASE}/context`).then(r => r.data),
  defaults: (country: string, copyFrom?: string) =>
    client.get<CompanySetupDraftBody>(`${BASE}/defaults`, { params: { country, ...(copyFrom ? { copyFrom } : {}) } }).then(r => r.data),
  drafts: () => client.get<CompanySetupDraftSummary[]>(`${BASE}/drafts`).then(r => r.data),
  draft: (id: string) => client.get<CompanySetupDraft>(`${BASE}/drafts/${id}`).then(r => r.data),
  createDraft: (body: CompanySetupDraftBody) => client.post<CompanySetupDraft>(`${BASE}/drafts`, body).then(r => r.data),
  /** 409 when `version` is stale: the draft was saved elsewhere. */
  saveDraft: (id: string, request: { version: number; step: number; body: CompanySetupDraftBody }) =>
    client.put<CompanySetupDraft>(`${BASE}/drafts/${id}`, request).then(r => r.data),
  discardDraft: (id: string) => client.delete(`${BASE}/drafts/${id}`).then(() => undefined),
  dryRun: (body: CompanySetupDraftBody) => client.post<CompanySetupPlan>(`${BASE}/dry-run`, body).then(r => r.data),
  /** The draft id is the idempotency key: a retried commit returns the first result (`replayed: true`). */
  commit: (draftId: string, version: number) =>
    client.post<CompanySetupResult>(`${BASE}`, { draftId, version }, { headers: { 'Idempotency-Key': draftId } }).then(r => r.data),
  readiness: (companyId: string) => client.get<ReadinessItem[]>(`${BASE}/readiness/${companyId}`).then(r => r.data),
};

/** HTTP status of an axios failure, or undefined. */
export function httpStatus(err: unknown): number | undefined {
  return (err as { response?: { status?: number } } | null)?.response?.status;
}
