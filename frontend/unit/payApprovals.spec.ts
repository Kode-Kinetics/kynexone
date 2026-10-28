import { expect, test } from '@playwright/test';
import { maskedAccount, payApprovalFailure, reasonError } from '../src/lib/payApprovals';

const failure = (status: number, code?: string) => payApprovalFailure({ response: { status, data: code ? { code } : {} } });

test.describe('pay approval view rules', () => {
  test('package_changed asks for a reload and never reads as a generic failure', () => {
    const f = failure(409, 'package_changed');
    expect(f.reload).toBe(true);
    expect(f.message).toContain('changed after you opened it');
  });
  test('a barred approver is told why, with no reload offered', () => {
    const f = payApprovalFailure({ response: { status: 403, data: { error: 'second_person_required' } } });
    expect(f.reload).toBe(false);
    expect(f.message).toContain('you entered it');
  });
  test('a missing or out-of-scope package reads the same and offers a reload', () => {
    expect(failure(404)).toEqual({ message: 'This package is not available to you.', reload: true });
    expect(failure(409, 'not_pending').reload).toBe(true);
  });
  test('an unreachable service and a throttle each get their own sentence', () => {
    expect(payApprovalFailure(new Error('network')).message).toContain('could not be reached');
    expect(failure(429).message).toContain('Too many attempts');
  });
  test('a reason is required for either decision and capped at 500', () => {
    expect(reasonError('')).not.toBeNull();
    expect(reasonError('   ')).not.toBeNull();
    expect(reasonError('Matches the signed offer')).toBeNull();
    expect(reasonError('x'.repeat(501))).not.toBeNull();
  });
  test('the account is masked to four characters', () => {
    expect(maskedAccount('7519')).toBe('•••• 7519');
    expect(maskedAccount(null)).toBe('');
  });
});
