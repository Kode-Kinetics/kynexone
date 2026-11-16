import { expect, test } from '@playwright/test';
import { cancellationHolders, canCancelLeave, canRequestLeaveCancellation, leaveStatus } from '../src/lib/essSelfService';

/**
 * An employee who asks to cancel APPROVED leave used to see "Cancellation requested" forever: nobody was told and
 * no screen decided it. The leave detail now says who holds it.
 */
test('a pending cancellation names the people who hold it', () => {
  expect(cancellationHolders(['Mona Manager', 'Hana HR'])).toEqual({
    key: 'Your cancellation request is with {names}.',
    params: { names: 'Mona Manager, Hana HR' },
  });
});

test('with nobody listed it still says the request is waiting, never a bare status', () => {
  for (const none of [[], undefined, null, ['  ', '']]) {
    expect(cancellationHolders(none as string[] | undefined | null)).toEqual({ key: 'Your cancellation request is waiting for a decision.' });
  }
});

test('Cancellation requested is its own status, and the employee cannot cancel it twice', () => {
  expect(leaveStatus('CancellationRequested')).toEqual({ label: 'Cancellation requested', tone: 'amber' });
  expect(canCancelLeave('CancellationRequested')).toBe(false);
  expect(canCancelLeave('Approved')).toBe(false);
});

test('approved leave that has not started yet can have its cancellation requested; nothing else can', () => {
  const now = new Date(2026, 10, 14, 15, 30); // 14 Nov 2026, local
  expect(canRequestLeaveCancellation('Approved', '2026-11-15', now)).toBe(true);
  expect(canRequestLeaveCancellation('Approved', '2027-01-07T00:00:00', now)).toBe(true);
  // Starts today, already started or finished: the employee cannot undo it themselves.
  expect(canRequestLeaveCancellation('Approved', '2026-11-14', now)).toBe(false);
  expect(canRequestLeaveCancellation('Approved', '2026-11-01', now)).toBe(false);
  // Only approved leave. A pending one is cancelled outright, a requested cancellation is already with the approver.
  for (const status of ['Submitted', 'PendingManagerApproval', 'Rejected', 'Cancelled', 'Withdrawn', 'CancellationRequested']) {
    expect(canRequestLeaveCancellation(status, '2027-01-07', now), status).toBe(false);
  }
  // No usable date: do not offer an action we cannot vouch for.
  expect(canRequestLeaveCancellation('Approved', '', now)).toBe(false);
});
