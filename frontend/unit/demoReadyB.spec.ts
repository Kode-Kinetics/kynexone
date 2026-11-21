import { test, expect } from '@playwright/test';
import { mayEditDraftRequisition, mayEditRejectedRequisition } from '../src/lib/requisitionDecision';

const holding = (...roles: string[]) => (role: string) => roles.includes(role);

test.describe('who may edit a Draft requisition (PUT is allowed for Draft or Rejected, roles Admin, HR Manager, HR Officer, Manager)', () => {
  test('the four allowed roles may edit a Draft', () => {
    for (const role of ['Admin', 'HR Manager', 'HR Officer', 'Manager']) expect(mayEditDraftRequisition({ status: 'Draft' }, holding(role))).toBe(true);
  });
  test('other roles may not', () => {
    for (const role of ['Employee', 'Recruiter', 'Finance Manager']) expect(mayEditDraftRequisition({ status: 'Draft' }, holding(role))).toBe(false);
    expect(mayEditDraftRequisition({ status: 'Draft' }, holding())).toBe(false);
  });
  test('only a Draft: not Submitted, PendingApproval, Approved, Rejected or Converted', () => {
    for (const status of ['Submitted', 'PendingApproval', 'Approved', 'Rejected', 'Converted']) expect(mayEditDraftRequisition({ status }, holding('Admin'))).toBe(false);
  });
  test('the rejected rule is unchanged', () => {
    expect(mayEditRejectedRequisition({ status: 'Rejected' }, holding('Admin'))).toBe(true);
    expect(mayEditRejectedRequisition({ status: 'Draft' }, holding('Admin'))).toBe(false);
  });
});
