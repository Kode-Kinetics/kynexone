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
  contract: { id: string; number: string; status: string; startDate: string; endDate: string | null } | null;
  salary: { effectiveDate: string; basicSalary: number; currency: string } | null;
  cells: PackageCell[];
  frozenRows: PackageFrozenRow[];
  blockReasons: BlockReason[];
  canFreeze: boolean;
  unverifiedRows: number;
  labels: Record<string, ComponentLabel>;
}

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
    client.post<{ frozen: boolean; alreadyFrozen: boolean; rowsWritten: number }>(
      `/api/entitlements/employees/${employeeId}/package/freeze`, { contractId },
      { headers: { 'Idempotency-Key': `package-freeze:${contractId}` } }).then((r) => r.data),
  confirm: (employeeId: number, contractId: string) =>
    client.post<{ confirmed: number }>(`/api/entitlements/employees/${employeeId}/package/confirm`, { contractId }).then((r) => r.data),
  mine: () => client.get<EssPackage>('/api/ess/package').then((r) => r.data),
};
