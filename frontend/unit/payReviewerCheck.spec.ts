import { expect, test } from '@playwright/test';
import type { CompanySetupPlan, ReadinessItem } from '../src/api/companySetup';
import type { PayApprovalSetup } from '../src/api/payApprovals';
import { emptyBody } from '../src/lib/companySetupWizard';
import { deadEndView, draftHasReviewer, eligiblePayReviewers, isReviewerWarning, payReviewerSatisfied, reviewerVerification } from '../src/lib/payReviewerCheck';

const setup = (over: Partial<PayApprovalSetup> = {}): PayApprovalSetup => ({
  independentReviewerCount: 1, readyForNewHires: true, pendingCount: 2, checkedCount: 2, strandedCount: 0, hasMore: false,
  items: [{ employeeId: 1, name: 'Layla', eligibleReviewers: [{ id: 'r', name: 'Fatima Approver' }] }], ...over,
});
const item = (status: ReadinessItem['status'], label = 'Fatima has not set a password yet'): ReadinessItem =>
  ({ key: 'payReviewer', label, labelAr: 'ع', status, scope: 'company', href: '/setup?tab=users&companyId=c1' });
const plan = (codes: string[] = []): CompanySetupPlan => ({ ok: true, items: [], problems: [], warnings: codes.map(code => ({ step: 6, field: 'payReviewer.mode', code, message: '' })) });

test('verification: checking until both answers are in, error when either call failed', () => {
  expect(reviewerVerification({ setup: null, readiness: null, loading: true, failed: false }).state).toBe('checking');
  expect(reviewerVerification({ setup: setup(), readiness: null, loading: false, failed: false }).state).toBe('checking');
  expect(reviewerVerification({ setup: setup(), readiness: [item('done')], loading: true, failed: false }).state).toBe('checking');
  expect(reviewerVerification({ setup: setup(), readiness: [item('done')], loading: false, failed: true }).state).toBe('error');
});

test('verification: ready only when a reviewer can really sign in; a created but unusable user is never ready', () => {
  expect(reviewerVerification({ setup: setup(), readiness: [item('done')], loading: false, failed: false })).toEqual({ state: 'ready', name: 'Fatima Approver' });
  const waiting = reviewerVerification({ setup: setup({ independentReviewerCount: 0, readyForNewHires: false, items: [] }), readiness: [item('todo')], loading: false, failed: false });
  expect(waiting.state).toBe('notReady');
  expect(waiting.state === 'notReady' && waiting.reason?.label).toBe('Fatima has not set a password yet');
  // The readiness item is the stricter test: a reviewer count alone, with the item still to do, is not ready.
  expect(reviewerVerification({ setup: setup(), readiness: [item('todo')], loading: false, failed: false }).state).toBe('notReady');
  // Without the readiness item (an older API) the pay-approval answer decides.
  expect(reviewerVerification({ setup: setup(), readiness: [], loading: false, failed: false }).state).toBe('ready');
  expect(reviewerVerification({ setup: setup({ readyForNewHires: false }), readiness: [], loading: false, failed: false })).toEqual({ state: 'notReady', reason: null });
});

test('review: satisfied by a requested reviewer or a live one; warned when none, or when the role is missing', () => {
  const b = emptyBody('SA');
  expect(draftHasReviewer(b)).toBe(false);
  expect(payReviewerSatisfied(b, plan(['pay_reviewer_missing']), null)).toBe(false);
  b.payReviewer = { mode: 'invite', fullNameEn: 'Omar', email: 'omar@acme.sa' };
  expect(payReviewerSatisfied(b, plan(), null)).toBe(true);
  expect(payReviewerSatisfied(b, plan(['pay_reviewer_role_missing']), null)).toBe(false);
  const none = emptyBody('SA');
  none.existingCompanyId = 'c1';
  expect(payReviewerSatisfied(none, plan(), { state: 'ready', name: null })).toBe(true);
  expect(payReviewerSatisfied(none, plan(), { state: 'notReady', reason: null })).toBe(false);
  expect(isReviewerWarning('pay_reviewer_missing') && isReviewerWarning('pay_reviewer_role_missing') && !isReviewerWarning('other')).toBe(true);
});

test('the picker offers active Finance Approvers who are not administrators', () => {
  const u = (id: string, roles: string[], isActive = true) => ({ id, roles, isActive, status: 'Active' });
  expect(eligiblePayReviewers([u('a', ['Finance Approver']), u('b', ['Finance Approver', 'Admin']), u('c', ['HR']), u('d', ['finance approver'], false)]).map(x => x.id)).toEqual(['a']);
});

test('dead end card: hidden at 0 and without a company, quiet while loading or failing, shown with a count', () => {
  expect(deadEndView({ companyId: null, setup: setup({ strandedCount: 3 }), loading: false, error: false }).state).toBe('hidden');
  expect(deadEndView({ companyId: 'c1', setup: null, loading: true, error: false }).state).toBe('loading');
  expect(deadEndView({ companyId: 'c1', setup: null, loading: false, error: true }).state).toBe('error');
  expect(deadEndView({ companyId: 'c1', setup: setup({ strandedCount: 0 }), loading: false, error: false }).state).toBe('hidden');
  expect(deadEndView({ companyId: 'c1', setup: setup({ strandedCount: 3, hasMore: true }), loading: false, error: false })).toEqual({ state: 'show', count: 3, hasMore: true });
});
