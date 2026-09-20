import client from './client';

// Benefits administration (api/compensation/benefits) and the employee's own view (api/ess/benefits).
// Dates are ISO yyyy-MM-dd strings (DateOnly on the server).

export interface BenefitPlan {
  id: string;
  companyId: string | null;
  code: string;
  name: string;
  planType: string;
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
  effectiveFrom: string;
  effectiveTo: string | null;
  isActive: boolean;
}

export interface BenefitEligibilityRuleInput {
  companyId: string | null;
  gradeId: string | null;
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
  checks: BenefitEligibilityCheckItem[];
}

export interface BenefitEnrollment {
  id: string;
  benefitPlanId: string;
  employeeId: number;
  companyId: string | null;
  employeeName: string;
  coverageTier: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  status: string;
}

export interface BenefitEnrollmentInput {
  benefitPlanId: string;
  employeeId: number;
  coverageTier: string | null;
  effectiveFrom: string;
  effectiveTo: string | null;
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

export interface BenefitEnrollmentDetail {
  enrollment: BenefitEnrollment;
  contributions: BenefitContribution[];
  links: BenefitPayrollDeductionLink[];
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
  planCode: string;
  planName: string;
  planType: string;
  currency: string;
  coverageTier: string;
  effectiveFrom: string;
  effectiveTo: string | null;
  status: string;
  currentEmployeeAmount: number | null;
  currentEmployerAmount: number | null;
  contributionFrequency: string | null;
}

export interface EssBenefits {
  employeeId: number;
  enrollments: EssBenefitEnrollment[];
}

const base = '/api/compensation/benefits';

export const benefitsApi = {
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
  if (e?.response?.status === 403) return 'Your role cannot perform this action. Benefits changes need Admin or HR Manager.';
  const data = e?.response?.data;
  if (typeof data === 'string' && data.trim()) return data;
  if (data && typeof data === 'object') {
    const d = data as { message?: string; error?: string; title?: string };
    return d.message ?? d.error ?? d.title ?? fallback;
  }
  return fallback;
}
