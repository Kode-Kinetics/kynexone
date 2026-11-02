import { test, expect } from '@playwright/test';
import { activationFormError, todayCalendarDay } from '../src/lib/employeeActivation';

test.describe('employee activation form', () => {
  test('defaults to the local calendar day', () => {
    expect(todayCalendarDay(new Date(2026, 9, 5, 23, 59))).toBe('2026-10-05');
  });
  test('accepts a back-dated effective date with a reason', () => {
    expect(activationFormError({ effectiveDate: '2026-10-01', reason: 'Joined last week' })).toBeNull();
  });
  test('rejects a missing or malformed date', () => {
    expect(activationFormError({ effectiveDate: '', reason: 'x' })).toBe('Choose the date the employee becomes active.');
    expect(activationFormError({ effectiveDate: '2026-02-31x', reason: 'x' })).toBe('Choose the date the employee becomes active.');
  });
  test('rejects a blank reason', () => {
    expect(activationFormError({ effectiveDate: '2026-10-01', reason: '   ' })).toBe('Enter a reason for the activation.');
  });
});
