import type { GradeLoanLimitRow, PublishGradeLoanLimitsRequest } from '../api/loanGovernance';
import type { LoanType } from '../api/loans';
import { draftProblem, inputFromDraft, type GradeLimitDraft } from './gradeLoanLimits';

/**
 * Loan limits in the Benefits by grade grid. The grid only hosts the editor: the rules, the validation and the
 * request shape are Limits by grade's own (lib/gradeLoanLimits.ts and PUT /api/finance/loans/grade-limits).
 */

const HR_LOAN_ROLES = ['Admin', 'HR Manager', 'HR Director'] as const;

/**
 * Whether the viewer may publish loan limits for this grid: the server's own gate for PUT grade-limits (an HR role, and
 * loans.policy_manage or employees.approve), and for the group-wide grid ('' company) group scope. It never widens it: Finance,
 * who holds loans.write, cannot, and nothing here is a new permission.
 */
export function canEditLoanLimits(i: { hasRole: (role: string) => boolean; hasPermission: (permission: string) => boolean; isGroupScope: boolean; companyId: string }): boolean {
  if (!HR_LOAN_ROLES.some(role => i.hasRole(role))) return false;
  if (!i.hasPermission('loans.policy_manage') && !i.hasPermission('employees.approve')) return false;
  return i.companyId ? true : i.isGroupScope;
}

/** The loan type a loan row stands for: the type whose facility component is that code. Never matched by name. */
export function loanTypeForComponent(types: readonly LoanType[] | null | undefined, componentCode: string): LoanType | undefined {
  const wanted = componentCode.toUpperCase();
  return types?.find(type => (type.entitlementComponentCode ?? '').toUpperCase() === wanted);
}

/** Where a loan type's rules live: Setup > Loan policies, opened on that loan type and company (the group grid has no company). */
export function loanLimitsLink(loanTypeId: string | null | undefined, companyId: string): string {
  const params = new URLSearchParams({ tab: 'loanPolicies' });
  if (loanTypeId) params.set('loanType', loanTypeId);
  if (companyId) params.set('companyId', companyId);
  return `/setup?${params.toString()}`;
}

const sameRow = (a: GradeLoanLimitRow, b: GradeLoanLimitRow) =>
  (a.cellId ?? null) === (b.cellId ?? null) && a.eligible === b.eligible && a.valueType === b.valueType
  && a.amount === b.amount && a.rate === b.rate && a.maxOutstandingAmount === b.maxOutstandingAmount;

/**
 * The grades among `gradeIds` whose limit is not what it was when the editor loaded them. Publishing starts a new version, so
 * a changed cell id means somebody published since, even with the same figures. A grade missing from the answer counts as changed.
 */
export function rowsChangedSince(opened: readonly GradeLoanLimitRow[], now: readonly GradeLoanLimitRow[], gradeIds: readonly string[]): string[] {
  return gradeIds.filter(id => {
    const before = opened.find(r => r.gradeId === id);
    const after = now.find(r => r.gradeId === id);
    return !before || !after || !sameRow(before, after);
  });
}

export type LoanPublishPlan =
  | { ok: true; request: PublishGradeLoanLimitsRequest; changed: GradeLimitDraft[] }
  | { ok: false; key: string; grade?: GradeLimitDraft };

/**
 * The same checks, in the same order, as Limits by grade's Publish: something changed, a start date, every changed row valid.
 * Only changed rows go into the request; the API leaves every other grade as it is.
 */
export function planLoanLimitPublish(i: { drafts: readonly GradeLimitDraft[]; loanTypeId: string; companyId: string; effectiveFrom: string }): LoanPublishPlan {
  const changed = i.drafts.filter(d => d.dirty);
  if (!changed.length) return { ok: false, key: 'Change at least one grade before publishing.' };
  if (!i.effectiveFrom) return { ok: false, key: 'Choose the date the new limits start.' };
  for (const draft of changed) {
    const problem = draftProblem(draft);
    if (problem) return { ok: false, key: problem, grade: draft };
  }
  return { ok: true, changed, request: { loanTypeId: i.loanTypeId, companyId: i.companyId || null, effectiveFrom: i.effectiveFrom, rows: changed.map(inputFromDraft) } };
}
