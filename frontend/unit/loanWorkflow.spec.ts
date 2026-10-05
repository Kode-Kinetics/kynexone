import { expect, test } from '@playwright/test';
import { canDecideLoan, loanErrorMessage } from '../src/lib/loanWorkflow';
import type { LoanApproval } from '../src/api/loans';

const step: LoanApproval = { id: 'step-2', loanId: 'loan-1', stepOrder: 2, approverRole: 'HR Manager', approvedByName: '', status: 'Pending', comments: '' };

test('loan decisions require ordered HR approval and deny Finance actors', () => {
  const reviewer = { id: 'reviewer', roles: ['HR Manager'] };
  expect(canDecideLoan(step, [{ ...step, id: 'step-1', stepOrder: 1 }, step], reviewer)).toBe(false);
  expect(canDecideLoan(step, [{ ...step, id: 'step-1', stepOrder: 1, status: 'Approved' }, step], reviewer)).toBe(true);
  expect(canDecideLoan(step, [step], { id: 'manager', roles: ['Manager'] })).toBe(false);
  expect(canDecideLoan(step, [step], { id: 'finance', roles: ['Finance'] })).toBe(false);
  expect(canDecideLoan(step, [step], { id: 'finance-approver', roles: ['Finance Approver'] })).toBe(false);
  expect(canDecideLoan({ ...step, approverRole: 'HR Director' }, [step], reviewer)).toBe(false);
  expect(canDecideLoan({ ...step, approverRole: 'HR Director' }, [step], { id: 'director', roles: ['HR Director'] })).toBe(true);
  expect(canDecideLoan({ ...step, status: 'Approved' }, [step], { id: 'admin', roles: ['Admin'] })).toBe(false);
});

test('legacy Finance steps require HR Manager even when a Finance user was assigned', () => {
  for (const approverRole of ['Finance', 'Finance Approver', 'Manager']) {
    const legacy = { ...step, approverRole, approverUserId: 'finance-user' };
    expect(canDecideLoan(legacy, [legacy], { id: 'finance-user', roles: ['Finance'] })).toBe(false);
    expect(canDecideLoan(legacy, [legacy], { id: 'finance-user', roles: ['Finance Approver'] })).toBe(false);
    expect(canDecideLoan(legacy, [legacy], { id: 'hr-user', roles: ['HR Manager'] })).toBe(true);
  }
});

test('structured API errors produce readable form text', () => {
  expect(loanErrorMessage({ response: { data: { message: 'Payment was already recorded.' } } }, 'Fallback')).toBe('Payment was already recorded.');
  expect(loanErrorMessage({ response: { data: { errors: { Amount: ['Amount exceeds outstanding balance.'], Reference: ['Reference is required.'] } } } }, 'Fallback')).toBe('Amount exceeds outstanding balance. Reference is required.');
  expect(loanErrorMessage({ response: { data: {} } }, 'Unable to record payment.')).toBe('Unable to record payment.');
});
