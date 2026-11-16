import { expect, test } from '@playwright/test';
import { grantedDays, leaveBalanceTotal } from '../src/lib/leaveBalanceDisplay';

// Demo R1 F6: a monthly-accrual balance read "16.25 of 0 days available".
test('a monthly-accrual balance (entitled 0) has no denominator', () => {
  expect(leaveBalanceTotal({ available: 16.25, entitled: 0 })).toBeNull();
});

test('a front-loaded balance reads out of its entitlement', () => {
  expect(leaveBalanceTotal({ available: 12, entitled: 21 })).toBe(21);
  expect(leaveBalanceTotal({ available: 21, entitled: 21 })).toBe(21);
});

test('a balance larger than the entitlement (carried forward) is not shown as "19 of 15"', () => {
  expect(leaveBalanceTotal({ available: 19, entitled: 15 })).toBeNull();
});

test('the days granted are the server figure, else the larger of entitled and accrued (never their sum)', () => {
  expect(grantedDays({ entitled: 0, accrued: 19.25 })).toBe(19.25);
  expect(grantedDays({ entitled: 21, accrued: 10 })).toBe(21);
  expect(grantedDays({ granted: 30, entitled: 21, accrued: 10 })).toBe(30);
});
