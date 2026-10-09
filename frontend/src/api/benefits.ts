import client from './client';
import type { ApprovalRequest } from './approvals';

// Benefits administration (api/compensation/benefits) and the employee's own view (api/ess/benefits).
// Dates are ISO yyyy-MM-dd strings (DateOnly on the server).

export interface BenefitPlan {
  id: string;
  companyId: string | null;
  code: string;
  name: string;
  planType: string;
  classification: 'Mandatory' | 'Contractual' | 'Discretionary';
  currency: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  requiresEnrollment: boolean;
  isActive: boolean;
}

export interface BenefitPlanCreateInput {
  companyId: string | null;
  code: string;
  name: string;
  planType: string;
  classification: 'Mandatory' | 'Contractual' | 'Discretionary';
  currency: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  requiresEnrollment: boolean;
  isActive: boolean;
}

export type BenefitPlanUpdateInput = Omit<BenefitPlanCreateInput, 'companyId' | 'code'>;

export interface BenefitEligibilityRule {
  id: string;
  benefitPlanId: string;
  companyId: string | null;
  gradeId: string | null;
  gradeMatchMode: 'Exact' | 'LevelAndAbove';
  tierName: string;
  maxBenefitAmount: number | null;
  limitPeriod: 'PerEnrollment' | 'Monthly' | 'Annual' | 'Lifetime';
  minimumServiceMonths: number;
  requireProbationCompleted: boolean;
  customCriteriaNote: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
}

export interface BenefitEligibilityRuleInput {
  companyId: string | null;
  gradeId: string | null;
  gradeMatchMode: 'Exact' | 'LevelAndAbove';
  tierName: string | null;
  maxBenefitAmount: number | null;
  limitPeriod: 'PerEnrollment' | 'Monthly' | 'Annual' | 'Lifetime';
  minimumServiceMonths: number;
  requireProbationCompleted: boolean;
  customCriteriaNote: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
}

export interface BenefitEligibilityCheckItem {
  key: 'plan_active' | 'company_scope' | 'plan_window' | 'eligibility_rules' | string;
  label: string;
  passed: boolean;
  detail: string;
}

export interface BenefitEligibilityCheck {
  benefitPlanId: string;
  currency: string;
  employeeId: number;
  employeeName: string;
  companyId: string | null;
  companyName: string | null;
  gradeId: string | null;
  gradeName: string | null;
  effectiveFrom: string;
  eligible: boolean;
  blockingReason: string | null;
  alreadyEnrolled: boolean;
  matchedRuleId: string | null;
  tierName: string | null;
  maximumBenefitAmount: number | null;
  limitPeriod: string | null;
  customCriteriaNote: string | null;
  checks: BenefitEligibilityCheckItem[];
}

export interface GradeBenefitDefault {
  benefitPlanId: string;
  code: string;
  name: string;
  planType: string;
  currency: string;
  eligible: boolean;
  blockingReason: string | null;
  eligibilityRuleId: string | null;
  entitlementTier: string | null;
  maximumBenefitAmount: number | null;
  limitPeriod: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
}

export interface BenefitExceptionInput {
  effectiveFrom: string;
  expectedUpdatedAtUtc: string | null;
  reason: string;
  coverageTier: string;
  entitlementTier: string;
  maximumBenefitAmount: number | null;
  requestedBenefitAmount: number | null;
  limitPeriod: string;
  status: 'Active' | 'Waived';
}

export interface BenefitException {
  id: string;
  reason: string;
  previousValuesJson: string;
  newValuesJson: string;
  createdAtUtc: string;
  createdBy: string | null;
  createdByName?: string | null;
}

export interface BenefitEnrollment {
  updatedAtUtc: string | null;
  id: string;
  benefitPlanId: string;
  employeeId: number;
  companyId: string | null;
  employeeName: string;
  coverageTier: string;
  eligibilityRuleId: string | null;
  assignmentSource: 'Manual' | 'GradeDefault' | 'IndividualException' | 'IndividualAdditional';
  hasException: boolean;
  exceptionReason: string | null;
  entitlementTier: string;
  maximumBenefitAmount: number | null;
  requestedBenefitAmount: number | null;
  limitPeriod: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  status: string;
}

export interface BenefitEnrollmentInput {
  exceptionReason?: string;
  benefitPlanId: string;
  employeeId: number;
  coverageTier: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  requestedBenefitAmount: number | null;
}

export interface AdditionalBenefitTerms {
  coverageTier: string;
  entitlementTier: string;
  maximumBenefitAmount: number | null;
  requestedBenefitAmount: number | null;
  limitPeriod: 'PerEnrollment' | 'Monthly' | 'Annual' | 'Lifetime';
  effectiveFrom: string;
  effectiveTo: string | null;
  reviewDate: string | null;
  reason: string;
  internalJustification: string;
  treatment: 'Coverage' | 'CashAllowance' | 'Reimbursement' | 'LoanEligibility' | 'OtherNonCash';
  plannedEmployerCost: number | null;
  plannedEmployeeCost: number | null;
  costFrequency: 'OneTime' | 'Monthly' | 'Annual' | null;
}

export interface AdditionalBenefitInput extends AdditionalBenefitTerms {
  employeeId: number;
  benefitPlanId: string;
  enrollmentId?: string;
  expectedUpdatedAtUtc?: string | null;
}

export interface AdditionalBenefitRequest {
  operation?: 'GrantOrAmend' | 'End' | 'Cancel';
  endDate?: string | null;
  id: string;
  status: string;
  employeeId: number;
  employeeName: string;
  benefitPlanId: string;
  planName: string;
  requestedByName?: string | null;
  createdAtUtc: string;
  terms: AdditionalBenefitInput;
  baseline?: BenefitEnrollment | null;
  currency?: string;
  approvalRequestId: string;
  appliedEnrollmentId?: string | null;
  approval?: ApprovalRequest | null;
}

export interface EmployeeBenefitPackageItem extends BenefitEnrollment {
  planName: string;
  planCode: string;
  currency: string;
  classification: BenefitPlan['classification'];
  effectiveStatus: 'Current' | 'Scheduled' | 'Expired' | 'Waived' | 'Cancelled';
  reviewDate: string | null;
  reviewRequired: boolean;
  reviewReasons: string[];
  approvalRequestId: string | null;
  grantReason: string | null;
  treatment?: AdditionalBenefitTerms['treatment'] | null;
  plannedEmployerCost?: number | null;
  plannedEmployeeCost?: number | null;
  costFrequency?: AdditionalBenefitTerms['costFrequency'];
}

export interface EmployeeBenefitPackage {
  employeeId: number;
  employeeName: string;
  gradeId: string | null;
  companyId: string | null;
  asOf: string;
  enrollments: EmployeeBenefitPackageItem[];
  additionalRequests: AdditionalBenefitRequest[];
}

export interface BenefitContribution {
  id: string;
  benefitEnrollmentId: string;
  benefitPlanId: string;
  employeeId: number;
  employeeAmount: number;
  employerAmount: number;
  frequency: string;
  payrollComponentCode: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
}

export interface BenefitContributionInput {
  employeeAmount: number;
  employerAmount: number;
  frequency: string | null;
  payrollComponentCode: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
}

export interface BenefitPayrollDeductionLink {
  id: string;
  benefitEnrollmentId: string;
  benefitContributionId: string;
  payrollDeductionId: string;
  payrollRunId: string;
  employeeId: number;
  linkedAmount: number;
}

export interface BenefitDeduction {
  linkId: string;
  payrollDeductionId: string;
  payrollRunId: string;
  year: number;
  month: number;
  runStatus: string;
  componentCode: string;
  componentName: string;
  deductionAmount: number;
  linkedAmount: number;
  source: string;
}

export interface BenefitEnrollmentDetail {
  enrollment: BenefitEnrollment;
  exceptions: BenefitException[];
  contributions: BenefitContribution[];
  links: BenefitPayrollDeductionLink[];
  deductions: BenefitDeduction[];
}

export interface BenefitDeductionCandidate {
  id: string;
  payrollRunId: string;
  year: number;
  month: number;
  runStatus: string;
  componentCode: string;
  componentName: string;
  amount: number;
  source: string;
}

export interface EssBenefitEnrollment {
  id: string;
  benefitPlanId: string;
  assignmentSource: 'Manual' | 'GradeDefault' | 'IndividualException' | 'IndividualAdditional';
  hasException: boolean;
  exceptionReason: string | null;
  planCode: string;
  planName: string;
  planType: string;
  currency: string;
  coverageTier: string;
  entitlementTier: string;
  maximumBenefitAmount: number | null;
  requestedBenefitAmount: number | null;
  limitPeriod: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  status: string;
  currentEmployeeAmount: number | null;
  currentEmployerAmount: number | null;
  contributionFrequency: string | null;
  deductions: BenefitDeduction[];
  reviewDate?: string | null;
  grantReason?: string | null;
  approvalRequestId?: string | null;
  effectiveStatus?: EmployeeBenefitPackageItem['effectiveStatus'];
  reviewRequired?: boolean;
  treatment?: AdditionalBenefitTerms['treatment'] | null;
  plannedEmployerCost?: number | null;
  plannedEmployeeCost?: number | null;
  costFrequency?: AdditionalBenefitTerms['costFrequency'];
}

export interface EssBenefits {
  employeeId: number;
  enrollments: EssBenefitEnrollment[];
}

const base = '/api/compensation/benefits';

export const benefitsApi = {
  employeePackage: (employeeId: number) => client.get<EmployeeBenefitPackage>(`${base}/employees/${employeeId}/package`).then(r => r.data),
  requestAdditional: (input: AdditionalBenefitInput) => client.post<AdditionalBenefitRequest>(`${base}/additional-grants`, input).then(r => r.data),
  requestBenefitEnd: (id: string, input: { endDate: string; reason: string; internalJustification: string; expectedUpdatedAtUtc: string | null }) => client.post<AdditionalBenefitRequest>(`${base}/enrollments/${id}/end-request`, input).then(r => r.data),
  additionalRequest: (id: string) => client.get<AdditionalBenefitRequest>(`${base}/additional-grants/${id}`).then(r => r.data),
  gradeDefaults: (params: { gradeId: string; companyId: string; effectiveFrom: string; probationEndDate?: string; confirmationDate?: string }) =>
    client.get<GradeBenefitDefault[]>(`${base}/grade-defaults`, { params }).then((r) => r.data),
  applyException: (enrollmentId: string, input: BenefitExceptionInput) =>
    client.patch<BenefitEnrollment>(`${base}/enrollments/${enrollmentId}/exception`, input).then((r) => r.data),
  listPlans: (companyId?: string) =>
    client.get<BenefitPlan[]>(`${base}/plans`, { params: { companyId } }).then((r) => r.data),
  createPlan: (input: BenefitPlanCreateInput) =>
    client.post<BenefitPlan>(`${base}/plans`, input).then((r) => r.data),
  updatePlan: (id: string, input: BenefitPlanUpdateInput) =>
    client.put<BenefitPlan>(`${base}/plans/${id}`, input).then((r) => r.data),

  listRules: (planId: string) =>
    client.get<BenefitEligibilityRule[]>(`${base}/plans/${planId}/eligibility`).then((r) => r.data),
  addRule: (planId: string, input: BenefitEligibilityRuleInput) =>
    client.post<BenefitEligibilityRule>(`${base}/plans/${planId}/eligibility`, input).then((r) => r.data),
  deactivateRule: (planId: string, ruleId: string) =>
    client.delete<BenefitEligibilityRule>(`${base}/plans/${planId}/eligibility/${ruleId}`).then((r) => r.data),

  checkEligibility: (planId: string, employeeId: number, effectiveFrom: string) =>
    client.get<BenefitEligibilityCheck>(`${base}/eligibility-check`, { params: { planId, employeeId, effectiveFrom } }).then((r) => r.data),

  listEnrollments: (params: { employeeId?: number; planId?: string; status?: string; companyId?: string } = {}) =>
    client.get<BenefitEnrollment[]>(`${base}/enrollments`, { params }).then((r) => r.data),
  getEnrollment: (id: string) =>
    client.get<BenefitEnrollmentDetail>(`${base}/enrollments/${id}`).then((r) => r.data),
  enroll: (input: BenefitEnrollmentInput) =>
    client.post<BenefitEnrollment>(`${base}/enrollments`, input).then((r) => r.data),

  addContribution: (enrollmentId: string, input: BenefitContributionInput) =>
    client.post<BenefitContribution>(`${base}/enrollments/${enrollmentId}/contributions`, input).then((r) => r.data),
  deductionCandidates: (enrollmentId: string) =>
    client.get<BenefitDeductionCandidate[]>(`${base}/enrollments/${enrollmentId}/deduction-candidates`).then((r) => r.data),
  linkDeduction: (enrollmentId: string, input: { benefitContributionId: string; payrollDeductionId: string; linkedAmount: number | null }) =>
    client.post<BenefitPayrollDeductionLink>(`${base}/enrollments/${enrollmentId}/payroll-deduction-links`, input).then((r) => r.data),

  mine: () => client.get<EssBenefits>('/api/ess/benefits').then((r) => r.data),
};

/** Server errors here are plain strings (BadRequest("...")) or { message } / { error } objects. */
export function benefitsErrorMessage(err: unknown, fallback: string): string {
  const e = err as { response?: { status?: number; data?: unknown } };
  if (e?.response?.status === 403) return 'Your role cannot perform this benefit action.';
  const data = e?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  if (data && typeof data === 'object') {
    const d = data as { message?: string; error?: string; title?: string };
    return d.message ?? d.error ?? d.title ?? fallback;
  }
  return fallback;
}
