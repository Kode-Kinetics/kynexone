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
  /** Whether this caller may appoint a company super admin. Absent on older servers: fall back to the role ceiling. */
  canGrantCompanyAdmin?: boolean | null;
  /**
   * Addendum A5: the active companies this caller may complete setup for — every active company for a group-scope
   * admin, only their own for a company super admin. Absent on a server without existing mode: the chooser then
   * offers create only.
   */
  manageableCompanies?: ManageableCompany[] | null;
}

/** One row of the Guided Setup chooser (Addendum A5). */
export interface ManageableCompany {
  id: string;
  legalNameEn: string;
  legalNameAr?: string | null;
  countryCode: string;
  /** Server-computed readiness, e.g. 5 of 9 done. */
  readinessDone: number;
  readinessTotal: number;
  /** The one open existing-mode draft for this company (A1), when someone has started one. */
  openDraftId?: string | null;
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
  /**
   * Existing mode only, read-only, filled by GET /defaults?existingCompanyId: the company's cutover status. The first
   * payroll month is editable only while it is "Planned" (A4); the server refuses a change otherwise.
   */
  cutoverStatus?: string | null;
}

/**
 * Existing mode (A2): a prefilled row that is already in the database carries its id, so the planner can tell
 * "edit this row" from "a new row with the same code". New rows have none.
 */
export interface ExistingRow { existingId?: string | null }

export interface CompanySetupLocation extends ExistingRow {
  code: string; nameEn: string; nameAr?: string;
  latitude?: number | null; longitude?: number | null; radiusMeters?: number | null;
}

export interface CompanySetupBranch extends ExistingRow {
  code: string; nameEn: string; nameAr?: string; city: string; timeZoneId: string; isHeadOffice: boolean;
  locations: CompanySetupLocation[];
}

export interface CompanySetupCostCentre extends ExistingRow { code: string; nameEn: string; nameAr?: string }
export interface CompanySetupDepartment extends ExistingRow { code: string; nameEn: string; nameAr?: string; parentCode?: string; branchCode?: string; costCentreCode?: string }
export interface CompanySetupDesignation extends ExistingRow { code: string; titleEn: string; titleAr?: string; departmentCode?: string; gradeCode?: string }
export interface CompanySetupGrade extends ExistingRow { code: string; nameEn: string; nameAr?: string; level: number; minSalary: number; midSalary: number; maxSalary: number }

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
  /** Addendum A1: null/absent = create a company; set = complete setup for that existing, active company. */
  existingCompanyId?: string | null;
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

export interface CompanySetupDraftSummary {
  id: string; legalNameEn: string; step: number; updatedAtUtc: string; createdByName: string;
  /** Set on an existing-mode draft (A1), so the drafts list can resume either kind at the right address. */
  existingCompanyId?: string | null;
}

export type PlanArea =
  | 'company' | 'branch' | 'location' | 'costCentre' | 'department' | 'designation' | 'grade'
  | 'employeeIdRule' | 'cutover' | 'holidayCalendar' | 'leavePolicy' | 'gccSetting' | 'workWeek' | 'companyAdmin';
/** A3: `update` = an existing row with changed fields; `unchanged` = an existing row left as it is. There are no deletes. */
export type PlanAction = 'create' | 'update' | 'unchanged' | 'reuse' | 'conflict';

export interface PlanItem {
  area: PlanArea; code: string; label: string; labelAr?: string | null; action: PlanAction; reason?: string | null;
  /** On `update`: the changed body fields as row-relative lower-camel keys (`nameEn`, `city`), so they can be named in
   *  the reader's language. Absent: the screen shows `reason`, which lists them in English. */
  changedFields?: string[] | null;
}
export interface Problem { step: number; field: string; code: string; message: string; messageAr?: string | null }
export interface CompanySetupPlan { ok: boolean; items: PlanItem[]; problems: Problem[]; warnings: Problem[] }

export interface ReadinessItem {
  key: string;
  label: string;
  labelAr?: string | null;
  status: 'done' | 'todo' | 'recommended';
  scope: 'company' | 'workspace';
  href: string;
  /** Only on the `companyAdmin` item while that admin has not signed in: the login to send a single-use sign-in link to
   *  (`usersApi.issuePasswordResetLink`). Absent everywhere else. */
  userId?: string | null;
}

/** Every refusal from /api/company-setup. `field` is dotted lower-camel with numeric indexes (`branches.0.code`). */
export interface CompanySetupErrorBody { code: string; message: string; messageAr?: string | null; field?: string | null; step?: number | null }

export interface CompanySetupResult {
  companyId: string;
  created: Partial<Record<PlanArea, number>>;
  reused: Partial<Record<PlanArea, number>>;
  readiness: ReadinessItem[];
  replayed: boolean;
  /** Existing mode: rows changed in place (A6 counts by action). */
  updated?: Partial<Record<PlanArea, number>> | null;
}

const BASE = '/api/company-setup';
// The wizard works on a company the switcher cannot name yet, and the server refuses a switcher
// header here (company_switcher_selected), so none of these calls carry it.
const WS = { workspaceScope: true } as const;

export const companySetupApi = {
  // A reply that is not the contract's shape (an older server, a proxy page) is "unavailable", never a crash.
  context: () => client.get<CompanySetupContext>(`${BASE}/context`, WS).then(r => {
    const d = r.data as Partial<CompanySetupContext> | null;
    if (!d || typeof d.canCreate !== 'boolean' || !Array.isArray(d.sisterCompanies) || !d.sharedCatalog) throw new Error('company setup context unavailable');
    return d as CompanySetupContext;
  }),
  defaults: (country: string, copyFrom?: string) =>
    client.get<CompanySetupDraftBody>(`${BASE}/defaults`, { ...WS, params: { country, ...(copyFrom ? { copyFrom } : {}) } }).then(r => r.data),
  /** A2: what an existing company has today, with anything missing filled from its country pack (rows with no existingId). */
  existingDefaults: (existingCompanyId: string) =>
    client.get<CompanySetupDraftBody>(`${BASE}/defaults`, { ...WS, params: { existingCompanyId } }).then(r => r.data),
  drafts: () => client.get<CompanySetupDraftSummary[]>(`${BASE}/drafts`, WS).then(r => (Array.isArray(r.data) ? r.data : [])),
  draft: (id: string) => client.get<CompanySetupDraft>(`${BASE}/drafts/${id}`, WS).then(r => r.data),
  createDraft: (body: CompanySetupDraftBody) => client.post<CompanySetupDraft>(`${BASE}/drafts`, body, WS).then(r => r.data),
  /** 409 when `version` is stale: the draft was saved elsewhere. */
  saveDraft: (id: string, request: { version: number; step: number; body: CompanySetupDraftBody }) =>
    client.put<CompanySetupDraft>(`${BASE}/drafts/${id}`, request, WS).then(r => r.data),
  discardDraft: (id: string) => client.delete(`${BASE}/drafts/${id}`, WS).then(() => undefined),
  dryRun: (body: CompanySetupDraftBody) => client.post<CompanySetupPlan>(`${BASE}/dry-run`, body, WS).then(r => r.data),
  /** The draft id is the idempotency key: a retried commit returns the first result (`replayed: true`). */
  commit: (draftId: string, version: number) =>
    client.post<CompanySetupResult>(`${BASE}`, { draftId, version }, { ...WS, headers: { 'Idempotency-Key': draftId } }).then(r => r.data),
  readiness: (companyId: string) => client.get<ReadinessItem[]>(`${BASE}/readiness/${companyId}`, WS).then(r => r.data),
};

/** HTTP status of an axios failure, or undefined. */
export function httpStatus(err: unknown): number | undefined {
  return (err as { response?: { status?: number } } | null)?.response?.status;
}
