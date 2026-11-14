/**
 * Payment readiness, as distinct from salary coverage.
 *
 * Coverage says who CAN be calculated. It said nothing about who can be PAID, so a tenant whose 250
 * active employees all had a salary read as ready — and the first validation of the run then raised
 * 250 missing-IBAN errors that block approval. GET /api/payroll/readiness now returns
 * `paymentPrerequisites`: the gaps that will block the run (per employee, with the next action),
 * kept apart from recommendations. An older API omits it; the UI then claims nothing either way.
 */
import type { PayrollReadiness } from '../api/payroll';

export interface PaymentPrerequisiteCount {
  code: string;
  label: string;
  nextAction: string;
  /** Employees with this gap. One employee can have several gaps. */
  count: number;
}

export interface PaymentCompanyPrerequisite {
  companyId: string | null;
  companyName: string | null;
  code: string;
  label: string;
  nextAction: string;
}

export interface PaymentPrerequisiteEmployee {
  employeeId: number;
  employeeCode: string;
  employeeName: string;
  companyId: string | null;
  blocking: string[];
  recommended: string[];
}

export interface PaymentPrerequisites {
  evaluatedEmployees: number;
  blockedEmployees: number;
  employeesWithRecommendations: number;
  attendanceChecked: boolean;
  companyBlocking: PaymentCompanyPrerequisite[];
  companyRecommended: PaymentCompanyPrerequisite[];
  blocking: PaymentPrerequisiteCount[];
  recommended: PaymentPrerequisiteCount[];
  employees: PaymentPrerequisiteEmployee[];
  employeesTruncated: boolean;
}

/** The readiness payload with the (newer, optional) payment fields. */
export type PayrollReadinessWithPrerequisites = PayrollReadiness & {
  isReadyToPay?: boolean;
  paymentPrerequisites?: PaymentPrerequisites | null;
};

/** What the headline says, as a translation key plus its parameters, next to the English text the checks read. */
interface HeadlineText {
  title: string; detail: string;
  titleKey: string; detailKey: string;
  params: { count: number; total: number };
}

export type PaymentReadinessHeadline =
  | { tone: 'unknown' }
  | { tone: 'empty' }
  | ({ tone: 'blocked' | 'attention' | 'clear' | 'settled' } & HeadlineText);

/** A run in one of these states has been locked: readiness drift can no longer stop its payment. */
const SETTLED_RUN_STATUSES = new Set(['Locked', 'Paid']);

export function isSettledRunStatus(status: string | null | undefined): boolean {
  return status != null && SETTLED_RUN_STATUSES.has(status);
}

/** English text for the checks that read the plain title; the screen translates through the keys instead. */
const num = (n: number) => n.toLocaleString('en-US');
const people = (n: number) => `${num(n)} ${n === 1 ? 'employee' : 'employees'}`;

/**
 * `runStatus` is the status of the period's Regular run (readiness.payrollRunStatus). Once that run is Locked or Paid,
 * gaps found now cannot block its payment, so they read as records to fix before next month, not as a block.
 */
export function paymentReadinessHeadline(p: PaymentPrerequisites | null | undefined, runStatus?: string | null): PaymentReadinessHeadline {
  if (!p) return { tone: 'unknown' };
  if (p.evaluatedEmployees === 0) return { tone: 'empty' };
  const total = num(p.evaluatedEmployees);
  const params = { count: p.blockedEmployees, total: p.evaluatedEmployees };
  if (p.blockedEmployees > 0 || p.companyBlocking.length > 0) {
    const employeeGaps = p.blockedEmployees > 0;
    if (isSettledRunStatus(runStatus)) {
      return {
        tone: 'settled',
        title: employeeGaps
          ? `${num(p.blockedEmployees)} of ${total} active employees have records to fix before next month`
          : 'Company setup needs attention before next month',
        detail: 'This run is already locked, so these gaps do not affect its payment. Fix them before the next payroll is processed.',
        titleKey: employeeGaps ? '{count} of {total} active employees have records to fix before next month' : 'Company setup needs attention before next month',
        detailKey: 'This run is already locked, so these gaps do not affect its payment. Fix them before the next payroll is processed.',
        params,
      };
    }
    return {
      tone: 'blocked',
      title: employeeGaps
        ? `${num(p.blockedEmployees)} of ${total} active employees cannot be paid yet`
        : 'Company setup blocks this payroll',
      detail: 'These gaps will block approval when the run is validated. Salary coverage alone does not make payroll ready to pay.',
      titleKey: employeeGaps ? '{count} of {total} active employees cannot be paid yet' : 'Company setup blocks this payroll',
      detailKey: 'These gaps will block approval when the run is validated. Salary coverage alone does not make payroll ready to pay.',
      params,
    };
  }
  if (p.employeesWithRecommendations > 0 || p.companyRecommended.length > 0) {
    const employeeFixes = p.employeesWithRecommendations > 0;
    return {
      tone: 'attention',
      title: `All ${total} active employees have what payment requires`,
      detail: employeeFixes
        ? `${people(p.employeesWithRecommendations)} still have recommended fixes. They do not block approval but will show as validation warnings.`
        : 'The company has recommended fixes. They do not block approval.',
      titleKey: 'All {total} active employees have what payment requires',
      detailKey: employeeFixes
        ? '{count, plural, one {# employee still has recommended fixes. They do not block approval but will show as validation warnings.} other {# employees still have recommended fixes. They do not block approval but will show as validation warnings.}}'
        : 'The company has recommended fixes. They do not block approval.',
      params: { count: p.employeesWithRecommendations, total: p.evaluatedEmployees },
    };
  }
  return {
    tone: 'clear',
    title: `All ${total} active employees have what payment requires`,
    detail: 'Salary, payroll profile, bank details and required records are in place for this period.',
    titleKey: 'All {total} active employees have what payment requires',
    detailKey: 'Salary, payroll profile, bank details and required records are in place for this period.',
    params,
  };
}

/** Plain-language label for a code, from the counts the API sent with it. */
export function prerequisiteLabel(p: PaymentPrerequisites, code: string): string {
  return [...p.blocking, ...p.recommended].find((c) => c.code === code)?.label ?? code;
}

/**
 * Drops the rules-engine "MissingSalarySetup" insight when LIVE readiness shows every active
 * employee has a salary. Insight rows are kept for history and outlive the condition that raised
 * them; the one that said "250 without salary" stayed open after all 250 were assigned.
 * Insights are tenant-wide, so only a tenant-wide readiness (no company filter) may retire one:
 * one fully covered company says nothing about the others.
 */
export function filterPayrollInsightsForReadiness<T extends { insightType: string }>(
  insights: T[],
  readiness: Pick<PayrollReadiness, 'companyId' | 'totalActiveEmployees' | 'employeesWithSalary' | 'salaryCoveragePercent'> | null,
): T[] {
  const salaryComplete = readiness != null
    && readiness.companyId == null
    && readiness.totalActiveEmployees > 0
    && readiness.employeesWithSalary >= readiness.totalActiveEmployees
    && readiness.salaryCoveragePercent >= 100;
  return salaryComplete ? insights.filter((insight) => insight.insightType !== 'MissingSalarySetup') : insights;
}
