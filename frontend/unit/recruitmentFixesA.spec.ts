import { expect, test } from '@playwright/test';
import { mayCloseOpening, mayDecideRequisition, mayEditRejectedRequisition } from '../src/lib/requisitionDecision';

// Walkthrough A6b: the author is offered neither Approve nor Reject on their own pending requisition (the API refuses both with
// maker_cannot_approve); one rule serves both buttons.
test('a pending requisition raised by the viewer offers no decision at all, Approve or Reject', () => {
  const mine = { status: 'PendingApproval', requestedByUserId: 'AAAA-1' };
  expect(mayDecideRequisition(mine, 'aaaa-1', true)).toBe(false);
  expect(mayDecideRequisition({ ...mine, status: 'Submitted' }, 'aaaa-1', true)).toBe(false);
});

test('someone else may decide a pending requisition only with the approve permission', () => {
  const theirs = { status: 'Submitted', requestedByUserId: 'aaaa-1' };
  expect(mayDecideRequisition(theirs, 'bbbb-2', true)).toBe(true);
  expect(mayDecideRequisition({ ...theirs, status: 'PendingApproval' }, 'bbbb-2', true)).toBe(true);
  expect(mayDecideRequisition(theirs, 'bbbb-2', false)).toBe(false);
});

test('a requisition that is not pending offers no decision', () => {
  for (const status of ['Draft', 'Approved', 'Rejected', 'Converted']) {
    expect(mayDecideRequisition({ status, requestedByUserId: 'aaaa-1' }, 'bbbb-2', true)).toBe(false);
  }
});

// Walkthrough A7b/A8: the API's Update and Submit allow Admin, HR Manager, HR Officer and Manager; Close allows Admin and HR Manager.
test('edit-and-resubmit is offered only on a rejected requisition and only to the roles the API allows', () => {
  const has = (...roles: string[]) => (role: string) => roles.includes(role);
  expect(mayEditRejectedRequisition({ status: 'Rejected' }, has('HR Officer'))).toBe(true);
  expect(mayEditRejectedRequisition({ status: 'Rejected' }, has('Manager'))).toBe(true);
  expect(mayEditRejectedRequisition({ status: 'Rejected' }, has('Recruiter'))).toBe(false);
  expect(mayEditRejectedRequisition({ status: 'Draft' }, has('Admin'))).toBe(false);
  expect(mayEditRejectedRequisition({ status: 'Approved' }, has('Admin'))).toBe(false);
});

test('closing an opening is offered to Admin and HR Manager only, and not on an opening that is already closed', () => {
  const has = (...roles: string[]) => (role: string) => roles.includes(role);
  expect(mayCloseOpening({ status: 'Open' }, has('Admin'))).toBe(true);
  expect(mayCloseOpening({ status: 'InProgress' }, has('HR Manager'))).toBe(true);
  expect(mayCloseOpening({ status: 'Open' }, has('HR Officer'))).toBe(false);
  expect(mayCloseOpening({ status: 'Open' }, has('Recruiter'))).toBe(false);
  expect(mayCloseOpening({ status: 'Closed' }, has('Admin'))).toBe(false);
});
