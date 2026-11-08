import { expect, test } from '@playwright/test';
import { canDecideDocument, documentStatusWords, documentVerificationState, rejectReasonOrNull } from '../src/lib/documentVerification';

test.describe('document verification helpers (G-004)', () => {
  test('a status maps to a state and to the words an employee reads', () => {
    expect(documentVerificationState('Pending')).toBe('pending');
    expect(documentVerificationState('verified')).toBe('verified');
    expect(documentVerificationState(' Rejected ')).toBe('rejected');
    expect(documentVerificationState('Approved')).toBe('approved');
    expect(documentVerificationState(undefined)).toBe('other');
    expect(documentStatusWords('Pending')).toBe('Waiting for HR to verify');
    expect(documentStatusWords('Verified')).toBe('Verified by HR');
    expect(documentStatusWords('Rejected')).toBe('Rejected by HR');
    expect(documentStatusWords('Approved')).toBe('Accepted by HR');
    expect(documentStatusWords(null)).toBe('Waiting for HR to verify');
  });

  test('a rejection needs a reason of 3 to 500 characters once trimmed', () => {
    expect(rejectReasonOrNull('')).toBeNull();
    expect(rejectReasonOrNull('   ab  ')).toBeNull();
    expect(rejectReasonOrNull('  Blurry scan  ')).toBe('Blurry scan');
    expect(rejectReasonOrNull('x'.repeat(500))).toBe('x'.repeat(500));
    expect(rejectReasonOrNull('x'.repeat(501))).toBeNull();
  });

  test('only a pending document, and only for a holder of employees.documents, can be decided', () => {
    expect(canDecideDocument({ approvalStatus: 'Pending' }, true)).toBe(true);
    expect(canDecideDocument({ approvalStatus: 'Pending' }, false)).toBe(false);
    for (const status of ['Verified', 'Rejected', 'Approved', 'Expired'])
      expect(canDecideDocument({ approvalStatus: status }, true)).toBe(false);
  });
});
