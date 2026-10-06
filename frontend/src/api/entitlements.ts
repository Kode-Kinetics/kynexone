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
}

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

export const entitlementsApi = {
  matrix: (params: { companyId?: string; asOf?: string }, signal?: AbortSignal) =>
    client.get<EntitlementMatrix>('/api/entitlements/matrix', { params, signal }).then(r => r.data),
  publish: (body: { companyId: string | null; effectiveFrom: string; cells: MatrixCellInput[] }, dryRun = false) =>
    client.put<PublishMatrixResult>('/api/entitlements/matrix', body, { params: dryRun ? { dryRun: true } : undefined }).then(r => r.data),
  setOffering: (body: { companyId: string; componentCode: string; offered: boolean; effectiveFrom: string }) =>
    client.put<MatrixOffering>('/api/entitlements/offerings', body).then(r => r.data),
  importLegacy: (commit: boolean, effectiveFrom?: string) =>
    client.post<LegacyImportResult>('/api/entitlements/matrix/import-legacy', null, { params: { commit, effectiveFrom } }).then(r => r.data),
};
