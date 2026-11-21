import { expect, test } from '@playwright/test';
import { signInCodeButtonKey, signInCodeHelpKey } from '../src/lib/handoffNotices';
import { activatedTileCount, draftStatusChipFor } from '../src/lib/newHireReview';
import { blockedReasonKey } from '../src/lib/payApprovals';

/** Rules that read the three new API fields; every one must treat a missing field as unknown (older API, older build). */
test.describe('sign-in code button follows the delivery field', () => {
  test('Email and PrintedSlip say exactly what will happen', () => {
    expect(signInCodeButtonKey('Email', true)).toBe('Email sign-in code');
    expect(signInCodeButtonKey('PrintedSlip', true)).toBe('Issue sign-in code and print slip');
    expect(signInCodeButtonKey('PrintedSlip', false)).toBe('Issue sign-in code and print slip');
  });
  test('without the field the earlier wording is kept', () => {
    expect(signInCodeButtonKey(undefined, true)).toBe('Issue sign-in code');
    expect(signInCodeButtonKey(undefined, false)).toBe('Give access');
    expect(signInCodeButtonKey('SomethingNew' as never, false)).toBe('Give access');
  });
  test('the generic help line is only the fallback for a company that emails and an API that does not say', () => {
    expect(signInCodeHelpKey('Email', true)).toBeNull();
    expect(signInCodeHelpKey('PrintedSlip', true)).toBeNull();
    expect(signInCodeHelpKey(undefined, false)).toBeNull();
    expect(signInCodeHelpKey(undefined, true)).toContain('emailed to the work email');
  });
});

test.describe('New hires Activated tile counts only Active employee records', () => {
  test('uses activeEmployees when present, the old count otherwise', () => {
    const base = { awaitingApproval: 0, draft: 0, activated: 3, rejected: 0, cancelled: 0 };
    expect(activatedTileCount({ ...base, activeEmployees: 1 })).toBe(1);
    expect(activatedTileCount({ ...base, activeEmployees: 0 })).toBe(0);
    expect(activatedTileCount(base)).toBe(3);
  });
  test('an approved draft whose record is not Active is not shown as Activated', () => {
    expect(draftStatusChipFor({ status: 'Activated', employeeStatus: 'Draft' }).label).toBe('Record created, not active yet');
    expect(draftStatusChipFor({ status: 'Activated', employeeStatus: 'Active' }).label).toBe('Activated');
    expect(draftStatusChipFor({ status: 'Activated', employeeStatus: null }).label).toBe('Activated');
    expect(draftStatusChipFor({ status: 'Activated' }).label).toBe('Activated');
    expect(draftStatusChipFor({ status: 'PendingHrApproval', employeeStatus: 'Draft' }).label).toBe('Waiting for HR approval');
  });
});

test.describe('pay approvals: why the caller cannot approve', () => {
  test('each reason has its own plain sentence', () => {
    expect(blockedReasonKey('EnteredByYou')).toBe('You entered this pay package.');
    expect(blockedReasonKey('YouAreTheEmployee')).toBe('You are the employee.');
    expect(blockedReasonKey('YouIssuedSignInCode')).toBe("You issued this employee's sign-in code.");
  });
  test('missing or unknown is unknown', () => {
    expect(blockedReasonKey(undefined)).toBeNull();
    expect(blockedReasonKey(null)).toBeNull();
    expect(blockedReasonKey('SomethingNew')).toBeNull();
  });
});
