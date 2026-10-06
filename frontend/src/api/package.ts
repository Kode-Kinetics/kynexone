import client from './client';

// Release A slice R2: the contract-year package. HR: /api/entitlements/employees/{id}/package (entitlements.read/manage).
// Employee: /api/ess/package (own package only). Dates are ISO yyyy-MM-dd strings (DateOnly on the server).

export type PackageSource = 'Salary' | 'ContractFrozen' | 'GradeStandard' | 'Facility';

/** One line of the package — the R0 shared contract (PackageLine). */
export interface PackageLine {
  componentCode: string;
  class: 'QiwaWage' | 'Contractual' | 'Facility' | string;
  floor: string;
  source: PackageSource;
  offered: boolean;
  eligible: boolean;
  valueType: string | null;
  amount: number | null;
  rate: number | null;
  monthlyCash: number | null;
  coverageTier: string | null;
  quantity: number | null;
  dependantScope: string;
  maxDependants: number | null;
  dependantsCovered: number;
  limitPeriod: string | null;
  gradeEntitlementId: string | null;
  employeeEntitlementId: string | null;
  isCompanyOverride: boolean;
  gradeStandardDiffers: boolean;
  reasonCode: string | null;
  maxOutstandingAmount: number | null;
  /** The cash figure the line comes to today (e.g. 3 × housing = 6,000; 25% of basic = 2,000). */
  resolvedAmount: number | null;
  /** When a line waiting on a criterion becomes eligible. */
  eligibleFrom: string | null;
  /** The grade's standard today (company cell where one exists); null when the grade has no cell. */
  standardValue: Record<string, unknown> | null;
}

export interface EmployeePackage {
  employeeId: number;
  gradeId: string | null;
  contractId: string | null;
  termEndsOn: string | null;
  asOf: string;
  lines: PackageLine[];
  blockCodes: string[];
}

export interface ComponentLabel { en: string; ar: string }

export interface PackageCell {
  id: string;
  componentCode: string;
  isCompanyOverride: boolean;
  eligible: boolean;
  valueType: string;
  amount: number | null;
  rate: number | null;
  maxOutstandingAmount: number | null;
  coverageTier: string | null;
  quantity: number | null;
  dependantScope: string;
  maxDependants: number | null;
  limitPeriod: string | null;
  minServiceMonths: number | null;
  afterProbation: boolean;
  nationalityScope: string;
  nationalityBasis: string | null;
  note: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
}

export interface PackageFrozenRow {
  id: string;
  componentCode: string;
  source: string;
  verificationState: 'Verified' | 'Unverified' | string;
  resolvedAmount: number | null;
  effectiveFrom: string;
  effectiveTo: string | null;
}

export interface BlockReason {
  code: string;
  titleEn: string; titleAr: string;
  whyEn: string; whyAr: string;
  fixEn: string; fixAr: string;
  ownerRole: string;
}

export interface EmployeePackageView {
  package: EmployeePackage;
  currency: string;
  grade: { id: string; code: string; name: string; nameAr: string | null; level: number } | null;
  company: { id: string; nameEn: string; nameAr: string } | null;
  contract: { id: string; number: string; status: string; startDate: string; endDate: string | null; workerNationalityClass: string | null } | null;
  salary: { effectiveDate: string; basicSalary: number; currency: string } | null;
  cells: PackageCell[];
  frozenRows: PackageFrozenRow[];
  blockReasons: BlockReason[];
  /** Every reason code on the screen, described (EN/AR title, why, fix). */
  reasons: Record<string, BlockReason>;
  /** Component code → the criterion a "not eligible yet" line is waiting for. */
  criteria: Record<string, string>;
  /** A direct "Fix" can succeed now (term not started, nothing blocking, no open proposal). */
  canFreeze: boolean;
  /** The term is running: its package is proposed, and another HR user confirms it. */
  canPropose: boolean;
  freezeStatus: FreezeStatus;
  dependantsOnFile: number;
  proposal: PackageProposal | null;
  labels: Record<string, ComponentLabel>;
}

export interface ProposedRow {
  componentCode: string; valueType: string; amount: number | null; rate: number | null; maxOutstandingAmount: number | null;
  coverageTier: string | null; quantity: number | null; dependantScope: string; maxDependants: number | null; limitPeriod: string | null;
  resolvedAmount: number | null; carriedFromId: string | null; verificationState: string;
}

/** "N of M benefits fixed", and why each of the rest is not (reasonCode null = it can be fixed now). */
export interface FreezeStatus {
  fixed: number;
  total: number;
  notFixed: Array<{ componentCode: string; reasonCode: string | null }>;
  blockedCode: string | null;
  possibleFrom: string | null;
}

export interface FreezeSkip { componentCode: string; code: string }

export interface PackageProposal {
  batchId: string;
  /** The viewer asked for the run, so another HR user must decide it. */
  requestedByYou: boolean;
  from: string;
  to: string | null;
  rows: ProposedRow[];
  skips: FreezeSkip[];
}

export interface FreezeOutcome {
  frozen: boolean; alreadyFrozen: boolean; rowsWritten: number;
  skipped: Array<{ componentCode: string; code: string; reason: BlockReason | null }>;
}

export interface ContractPackageStatus { contractId: string; employeeId: number; nextAction: 'proposeBenefits' | 'reviewProposal' }

export type DependantRelationship = 'Spouse' | 'Child' | 'Parent' | 'Other';
export interface Dependant { id: string; fullName: string; relationship: DependantRelationship; dateOfBirth: string | null; nationalId: string }
export interface DependantInput { fullName: string; relationship: DependantRelationship; dateOfBirth: string | null; nationalId: string | null }

export type EssGroup = 'pay' | 'contract' | 'facility';

export interface EssPackageLine {
  componentCode: string;
  labelEn: string;
  labelAr: string;
  group: EssGroup;
  eligible: boolean;
  offered: boolean;
  fixed: boolean;
  valueType: string | null;
  amount: number | null;
  rate: number | null;
  monthlyCash: number | null;
  coverageTier: string | null;
  quantity: number | null;
  dependantScope: string;
  maxDependants: number | null;
  dependantsCovered: number;
  limitPeriod: string | null;
  reasonCode: string | null;
  reasonCriterion: string | null;
  eligibleFrom: string | null;
  resolvedAmount: number | null;
  why: { basis: 'salary' | 'contract' | 'grade' | 'policy'; gradeName: string | null; gradeNameAr: string | null; companyRule: boolean; since: string | null; until: string | null };
}

export interface EssPackage {
  asOf: string;
  currency: string;
  gradeName: string | null;
  gradeNameAr: string | null;
  companyName: string | null;
  companyNameAr: string | null;
  termStartsOn: string | null;
  fixedUntil: string | null;
  renewalReviewOpensOn: string | null;
  dependantsOnFile: number;
  lines: EssPackageLine[];
}

export const packageApi = {
  forEmployee: (employeeId: number, asOf?: string) =>
    client.get<EmployeePackageView>(`/api/entitlements/employees/${employeeId}/package`, { params: asOf ? { asOf } : undefined }).then((r) => r.data),
  freeze: (employeeId: number, contractId: string) =>
    client.post<FreezeOutcome>(
      `/api/entitlements/employees/${employeeId}/package/freeze`, { contractId },
      { headers: { 'Idempotency-Key': `package-freeze:${contractId}` } }).then((r) => r.data),
  /** The one next action for each active contract's benefits (contract register). Contracts needing none are omitted. */
  contractStatus: (contractIds: string[]) =>
    client.get<ContractPackageStatus[]>('/api/entitlements/contracts/package-status', { params: { ids: contractIds },
      paramsSerializer: { indexes: null } }).then((r) => r.data),
  propose: (employeeId: number, contractId: string) =>
    client.post<{ jobId: string }>(`/api/entitlements/employees/${employeeId}/package/propose`, { contractId }).then((r) => r.data),
  confirmProposal: (batchId: string, contractId: string, documentId: string) =>
    client.post<{ confirmed: number }>(`/api/entitlements/package/proposals/${batchId}/confirm`, { contractId, documentId }).then((r) => r.data),
  rejectProposal: (batchId: string, contractId: string, reason: string) =>
    client.post<{ rejected: boolean }>(`/api/entitlements/package/proposals/${batchId}/reject`, { contractId, reason }).then((r) => r.data),
  dependants: (employeeId: number) => client.get<Dependant[]>(`/api/entitlements/employees/${employeeId}/dependants`).then((r) => r.data),
  addDependant: (employeeId: number, input: DependantInput) =>
    client.post<Dependant>(`/api/entitlements/employees/${employeeId}/dependants`, input).then((r) => r.data),
  updateDependant: (employeeId: number, id: string, input: DependantInput) =>
    client.put<Dependant>(`/api/entitlements/employees/${employeeId}/dependants/${id}`, input).then((r) => r.data),
  removeDependant: (employeeId: number, id: string) => client.delete(`/api/entitlements/employees/${employeeId}/dependants/${id}`),
  mine: () => client.get<EssPackage>('/api/ess/package').then((r) => r.data),
};
