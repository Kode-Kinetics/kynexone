import { expect, test } from '@playwright/test';
import { cancellationHolders, canCancelLeave, leaveStatus } from '../src/lib/essSelfService';

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
