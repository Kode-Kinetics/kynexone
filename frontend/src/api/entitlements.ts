import client from './client';

/** Release A slice R1 — benefits by grade. Mirrors backend Infrastructure/Entitlements/EntitlementMatrixModels.cs. */

export type ComponentGroup = 'Wage' | 'Contract' | 'Facility';
export type OfferingMode = 'Group' | 'Adopted' | 'Tailored' | 'Skipped';
export type MatrixValueType =
  | 'Amount' | 'PercentOfBasic' | 'MultipleOfBasic' | 'MultipleOfGross' | 'MultipleOfHousing'
  | 'InKind' | 'CoverageTier' | 'Quantity' | 'EligibilityOnly';
export type DependantScope = 'None' | 'Spouse' | 'Children' | 'Family';
export type NationalityScope = 'Any' | 'Saudi' | 'NonSaudi';

/** A legal block reason in both languages. The UI renders the sentences, never the code. */
export interface BlockReason {
  code: string; titleEn: string; titleAr: string; whyEn: string; whyAr: string; fixEn: string; fixAr: string;
}

export interface MatrixGrade { id: string; code: string; name: string; nameAr: string | null; level: number }

export interface MatrixComponent {
  code: string; nameEn: string; nameAr: string; class: string; floor: string; group: ComponentGroup;
  allowedValueTypes: MatrixValueType[]; allowedCoverageTiers: string[]; allowedDependantScopes: DependantScope[];
  allowedLimitPeriods: string[]; defaultLimitPeriod: string | null;
  isFloor: boolean; canBeSkipped: boolean; isLoanFacility: boolean; floorReason: BlockReason | null;
  /** A benefit the customer added themselves, removable until it is used: a non-paying contract benefit (BEN_) or a
   * monthly cash allowance paid in salary (ALW_, in the Wage group). */
  isCustom?: boolean;
  /** Only on a monthly cash allowance (ALW_*): whether Finance still has to confirm it, and the GL account and end of service setting in effect. */
  cashAllowance?: CashAllowanceInfo | null;
  /** Non-blocking cautions the server raises when an allowance is created, confirmed or corrected. */
  warnings?: AllowanceWarning[] | null;
}

/** Why a cash allowance is left out of end of service pay. `Other` needs a note. */
export type EosbExclusionCategory = 'Reimbursement' | 'Discretionary' | 'NonRecurring' | 'Other';

/** Mirrors backend CashAllowanceInfoDto. */
export interface CashAllowanceInfo {
  financePending: boolean; financeConfirmedAtUtc: string | null; glDriverKey: string | null; eosbIncluded: boolean;
  eosbExclusionCategory: EosbExclusionCategory | null; eosbExclusionReason: string | null; effectiveFrom: string | null;
}

/** Mirrors backend AllowanceWarningDto: `message` is plain words (English). */
export interface AllowanceWarning { code: string; message: string }

export interface MatrixCell {
  gradeId: string; componentCode: string; cellId: string; isCompanyOverride: boolean; inherited: boolean; eligible: boolean;
  valueType: MatrixValueType; amount: number | null; rate: number | null; maxOutstandingAmount: number | null;
  coverageTier: string | null; quantity: number | null; dependantScope: DependantScope; maxDependants: number | null;
  limitPeriod: string | null; minServiceMonths: number | null; afterProbation: boolean; nationalityScope: NationalityScope;
  nationalityBasis: string | null; sourceRule: string | null; note: string | null; effectiveFrom: string; effectiveTo: string | null;
  nextChangeOn: string | null;
}

export interface MatrixOffering {
  componentCode: string; mode: OfferingMode; offered: boolean; skippedFrom: string | null; changesOn: string | null; canBeSkipped: boolean;
}

/** No value in force. `scheduledFrom`: a published value starts on that date. */
export interface MatrixGap { gradeId: string; componentCode: string; scheduledFrom: string | null }

export interface EntitlementMatrix {
  asOf: string; today: string; companyId: string | null; currency: string | null;
  grades: MatrixGrade[]; components: MatrixComponent[]; cells: MatrixCell[]; offerings: MatrixOffering[]; gaps: MatrixGap[];
}

/** One cell to publish. `useGroupDefault` (company view only) drops the company's own value. */
export interface MatrixCellInput {
  gradeId: string; componentCode: string; eligible: boolean; valueType?: MatrixValueType | null;
  amount?: number | null; rate?: number | null; coverageTier?: string | null; quantity?: number | null;
  dependantScope?: DependantScope | null; maxDependants?: number | null; limitPeriod?: string | null;
  minServiceMonths?: number | null; afterProbation?: boolean; nationalityScope?: NationalityScope | null;
  nationalityBasis?: string | null; note?: string | null; useGroupDefault?: boolean;
}

export interface PublishMatrixResult {
  dryRun: boolean; published: number; unchanged: number; reverted: number;
  affectedNow: number; affectedAtRenewal: number; gapsRemaining: number; matrix: EntitlementMatrix | null;
  /** Values that had not started yet and that nothing used, replaced on their own start date. */
  superseded?: number;
}

export interface MatrixCellError {
  gradeId: string; componentCode: string; code: string; message: string; reason: BlockReason | null;
}

export interface LegacyImportItem {
  source: 'PayScale' | 'Eligibility'; sourceId: string; gradeId: string; gradeCode: string; gradeName: string; gradeNameAr: string | null;
  sourceCode: string; sourceName: string; detail: string | null; componentCode: string | null;
  outcome: 'Import' | 'Skip'; reasonCode: string | null; reason: string | null; cell: MatrixCellInput | null;
}

export interface LegacyImportResult {
  committed: boolean; effectiveFrom: string; toImport: number; skipped: number; imported: number; items: LegacyImportItem[];
}

/** What the customer adds: a contract benefit (default) or a monthly cash allowance paid in salary. */
export type BenefitKind = 'Contract' | 'Cash';

/** GET /api/entitlements/components/cash-gl-drivers. `reasonCode` is null when usable. */
export interface CashGlDriver {
  key: string; label: string; labelAr: string | null; usable: boolean;
  reasonCode: 'reserved' | 'employer_pair' | 'unmapped' | 'protected_account' | null; reason: string | null;
}

/** `glDriverKey` is required for Cash; `eosbExclusionReason` for Cash with `eosbIncluded` false. GOSI and WPS are fixed by the server. */
export interface CreateBenefitBody {
  nameEn?: string | null; nameAr?: string | null; kind: BenefitKind;
  glDriverKey?: string | null; eosbIncluded?: boolean; eosbExclusionReason?: string | null;
  /** Why it is left out of end of service pay (when `eosbIncluded` is false). `Other` needs `eosbExclusionReason`. */
  eosbExclusionCategory?: EosbExclusionCategory | null;
}

/** POST .../components/{code}/confirm: every field optional; left out, the allowance is confirmed as it was entered. */
export interface ConfirmAllowanceBody {
  glDriverKey?: string | null; eosbIncluded?: boolean; eosbExclusionCategory?: EosbExclusionCategory | null; eosbExclusionReason?: string | null;
}

/** POST .../components/{code}/correct: from the first day of `effectiveFrom`'s month; `reason` is required. */
export interface CorrectAllowanceBody extends ConfirmAllowanceBody { effectiveFrom: string; reason: string }

/** A draft payroll run calculated with the setting a correction replaced: process it again. */
export interface StaleRun { id: string; year: number; month: number; status: string }
export interface CorrectAllowanceResult { component: MatrixComponent; staleRuns: StaleRun[] }

export const entitlementsApi = {
  matrix: (params: { companyId?: string; asOf?: string }, signal?: AbortSignal) =>
    client.get<EntitlementMatrix>('/api/entitlements/matrix', { params, signal }).then(r => r.data),
  publish: (body: { companyId: string | null; effectiveFrom: string; cells: MatrixCellInput[] }, dryRun = false) =>
    client.put<PublishMatrixResult>('/api/entitlements/matrix', body, { params: dryRun ? { dryRun: true } : undefined }).then(r => r.data),
  /** Adds a benefit of the customer's own (needs access to every company). Returns it as a grid row. */
  /** The earning GL accounts a cash allowance may post to, each judged by the same rule the add applies. Needs only entitlements.manage:
   *  key, label and the verdict, never account numbers. */
  cashGlDrivers: () =>
    client.get<CashGlDriver[]>('/api/entitlements/components/cash-gl-drivers').then(r => r.data),
  addBenefit: (body: CreateBenefitBody) =>
    client.post<MatrixComponent>('/api/entitlements/components', body).then(r => r.data),
  /** Removes a benefit the customer added and nobody has used yet (409 benefit_in_use otherwise). */
  removeBenefit: (code: string) =>
    client.post<{ removed: string }>(`/api/entitlements/components/${encodeURIComponent(code)}/remove`).then(r => r.data),
  /** Finance confirms a cash allowance somebody without the Finance permission added (payroll.approve; never the person who added it). */
  confirmAllowance: (code: string, body: ConfirmAllowanceBody) =>
    client.post<MatrixComponent>(`/api/entitlements/components/${encodeURIComponent(code)}/confirm`, body).then(r => r.data),
  /** Finance changes a confirmed allowance's GL account and/or end of service setting from a month on. */
  correctAllowance: (code: string, body: CorrectAllowanceBody) =>
    client.post<CorrectAllowanceResult>(`/api/entitlements/components/${encodeURIComponent(code)}/correct`, body).then(r => r.data),
  setOffering: (body: { companyId: string; componentCode: string; offered: boolean; effectiveFrom: string }) =>
    client.put<MatrixOffering>('/api/entitlements/offerings', body).then(r => r.data),
  importLegacy: (commit: boolean, effectiveFrom?: string) =>
    client.post<LegacyImportResult>('/api/entitlements/matrix/import-legacy', null, { params: { commit, effectiveFrom } }).then(r => r.data),
};
