import type { CompanySetupDraftBody, CompanySetupPlan, ReadinessItem } from '../api/companySetup';
import type { PayApprovalSetup } from '../api/payApprovals';

/**
 * What the Approvers step and the review say about pay approval. Pure, so the rules are unit-tested
 * (unit/payReviewerCheck.spec.ts). Rule one: "ready" needs a login that can actually review pay today, never just a
 * user that exists. A person who has not set a password, or finished two-step sign-in, is "not ready" with the reason.
 */

export type ReviewerVerification =
  | { state: 'checking' }
  | { state: 'ready'; name: string | null }
  | { state: 'notReady'; reason: ReadinessItem | null }
  | { state: 'error' };

export function reviewerVerification(input: {
  setup: PayApprovalSetup | null; readiness: readonly ReadinessItem[] | null; loading: boolean; failed: boolean;
}): ReviewerVerification {
  if (input.failed) return { state: 'error' };
  const { setup, readiness } = input;
  if (input.loading || !setup || !readiness) return { state: 'checking' };
  const item = readiness.find(i => i.key === 'payReviewer') ?? null;
  // The readiness item is the stricter test (it leaves out administrators); without it the pay-approval answer decides.
  const usable = setup.independentReviewerCount > 0 && (item ? item.status === 'done' : setup.readyForNewHires);
  if (usable) {
    const named = setup.items.flatMap(i => i.eligibleReviewers)[0]?.name ?? null;
    return { state: 'ready', name: named };
  }
  return { state: 'notReady', reason: item && item.status !== 'done' ? item : null };
}

const NONE = new Set(['', 'groupOnly']);
/** True when the draft asks for a reviewer to be created or granted. */
export const draftHasReviewer = (body: Pick<CompanySetupDraftBody, 'payReviewer'>): boolean => !NONE.has(body.payReviewer?.mode ?? '');

/** The server could not or would not set the requested reviewer up (a missing role), so the request is as good as none. */
const UNSATISFIED_WARNINGS = new Set(['pay_reviewer_missing', 'pay_reviewer_role_missing']);

/**
 * Review: is pay approval going to work after this save? Satisfied when a reviewer is requested and the server raised no
 * warning against it, or (completing an existing company) a usable reviewer already exists. Anything else warns; it never
 * blocks creating the company.
 */
export function payReviewerSatisfied(body: CompanySetupDraftBody, plan: CompanySetupPlan | null, live: ReviewerVerification | null): boolean {
  if (plan?.warnings.some(w => w.code === 'pay_reviewer_role_missing')) return false;
  if (draftHasReviewer(body)) return true;
  return live?.state === 'ready';
}

/** Server warnings the Approvers card and review already explain, so the generic list does not repeat them. */
export const isReviewerWarning = (code: string): boolean => UNSATISFIED_WARNINGS.has(code);

export interface DeadEndView { state: 'hidden' | 'loading' | 'error' | 'show'; count: number; hasMore: boolean }

/** The dashboard dead-end card: hidden at 0, quiet while loading or on error, shown only for a real stranded count. */
export function deadEndView(input: { companyId: string | null; setup: PayApprovalSetup | null; loading: boolean; error: boolean }): DeadEndView {
  if (!input.companyId) return { state: 'hidden', count: 0, hasMore: false };
  if (input.error) return { state: 'error', count: 0, hasMore: false };
  if (!input.setup) return { state: input.loading ? 'loading' : 'hidden', count: 0, hasMore: false };
  const count = input.setup.strandedCount;
  return count > 0 ? { state: 'show', count, hasMore: input.setup.hasMore } : { state: 'hidden', count: 0, hasMore: false };
}

/** The role the pay reviewer holds, fixed by the server (PayReviewerRole.Name). */
export const PAY_REVIEWER_ROLE = 'Finance Approver';

/**
 * Existing users the picker offers: active, holding the Finance Approver role, not an administrator (the reviewer must
 * be a separate person). The planner re-checks all of it (existing_user_cannot_approve_pay, pay_reviewer_is_administrator).
 */
export function eligiblePayReviewers<U extends { isActive: boolean; status?: string | null; roles?: readonly string[] | null }>(users: readonly U[]): U[] {
  const has = (u: U, role: string) => (u.roles ?? []).some(r => r.trim().toLowerCase() === role.toLowerCase());
  return users.filter(u => u.isActive && (u.status ?? 'Active') === 'Active' && has(u, PAY_REVIEWER_ROLE) && !has(u, 'Admin'));
}
