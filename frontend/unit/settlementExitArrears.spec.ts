import { test, expect } from '@playwright/test';
import { exitLeaveMoney } from '../src/lib/settlementExitArrears';

// The settlement detail carries the unpaid-leave money no run collected before the employee left. The screen shows it as
// its own line with the amount, so a leaver's outstanding arrears are not lost; nothing is shown when there is none.

test('arrears owed by the employee are returned with the months they belong to', () => {
  const m = exitLeaveMoney({ owedByEmployee: 350.5, owedToEmployee: 0, months: [{ period: '2026-09', amount: 200 }, { period: '2026-10', amount: 150.5 }] });
  expect(m).toEqual({ owedByEmployee: 350.5, owedToEmployee: 0, months: ['2026-09', '2026-10'] });
});

test('a leave refund still owed to the employee is kept apart from arrears', () => {
  const m = exitLeaveMoney({ owedByEmployee: 0, owedToEmployee: 80, months: [{ period: '2026-10', amount: -80 }] });
  expect(m?.owedToEmployee).toBe(80);
  expect(m?.owedByEmployee).toBe(0);
});

test('nothing owed either way, or no figure at all, shows no line', () => {
  expect(exitLeaveMoney({ owedByEmployee: 0, owedToEmployee: 0, months: [] })).toBeNull();
  expect(exitLeaveMoney(null)).toBeNull();
  expect(exitLeaveMoney(undefined)).toBeNull();
});
