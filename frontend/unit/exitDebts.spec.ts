import { test, expect } from '@playwright/test';
import { exitDebtSummary, moneyByCurrency, type ExitDebt } from '../src/lib/exitDebts';

const debt = (over: Partial<ExitDebt>): ExitDebt => ({
  kind: 'Loan', id: 'l1', reference: 'LN-1', repaymentMethod: 'BankTransfer', held: false,
  outstandingBalance: 1000, currency: 'SAR', plannedRecovery: 0, ...over,
});

test.describe('open debts at exit', () => {
  test('totals every open loan and advance, and counts a held loan separately', () => {
    const s = exitDebtSummary([debt({}), debt({ id: 'l2', held: true, outstandingBalance: 800 }), debt({ kind: 'Advance', id: 'a1', outstandingBalance: 600, repaymentMethod: null })]);
    expect(s.count).toBe(3);
    expect(s.byCurrency).toEqual([{ currency: 'SAR', amount: 2400 }]);
    expect(s.heldByCurrency).toEqual([{ currency: 'SAR', amount: 800 }]);
  });

  test('never adds two currencies into one figure', () => {
    const s = exitDebtSummary([debt({ currency: 'SAR' }), debt({ id: 'l2', currency: 'AED', outstandingBalance: 50 })]);
    expect(s.byCurrency).toEqual([{ currency: 'AED', amount: 50 }, { currency: 'SAR', amount: 1000 }]);
  });

  test('a debt with no currency is shown in the fallback currency', () => {
    expect(exitDebtSummary([debt({ currency: null })], 'SAR').byCurrency).toEqual([{ currency: 'SAR', amount: 1000 }]);
  });

  test('nothing open reads as nothing', () => {
    expect(exitDebtSummary([])).toEqual({ count: 0, byCurrency: [], heldByCurrency: [] });
  });
});

test.describe('money by currency', () => {
  test('one currency is one figure, several are joined, none is null', () => {
    const fmt = (n: number, c: string) => `${c} ${n}`;
    expect(moneyByCurrency([{ currency: 'SAR', amount: 10 }], fmt)).toBe('SAR 10');
    expect(moneyByCurrency([{ currency: 'AED', amount: 5 }, { currency: 'SAR', amount: 10 }], fmt)).toBe('AED 5 + SAR 10');
    expect(moneyByCurrency([], fmt)).toBeNull();
  });
});
