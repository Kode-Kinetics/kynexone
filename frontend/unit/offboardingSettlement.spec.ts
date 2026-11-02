import { test, expect } from '@playwright/test';
import type { FinalSettlementListRow } from '../src/api/payroll';
import { isSettlementCleared, settlementFor, settlementStage } from '../src/lib/offboardingSettlement';

const row = (over: Partial<FinalSettlementListRow>): FinalSettlementListRow => ({
  id: 's1', employeeId: 1, employeeCode: 'E1', employeeName: 'A', companyId: null, offboardingId: 'o1',
  lastWorkingDay: '2026-10-31', settlementDueDate: '2026-11-15', terminationReason: 'Resignation',
  status: 'Draft', currency: 'SAR', netPayable: 100, payrollRunId: null, paymentBatchId: null, paidAtUtc: null,
  isOverdue: false, overdueDays: 0, ...over,
});

test.describe('offboarding settlement link', () => {
  test('matches by offboarding and ignores cancelled settlements', () => {
    const rows = [row({ id: 'a', status: 'Cancelled' }), row({ id: 'b', offboardingId: 'o2' }), row({ id: 'c', status: 'Approved' })];
    expect(settlementFor(rows, 'o1')?.id).toBe('c');
    expect(settlementFor(rows, 'o9')).toBeNull();
  });
  test('prefers the newest settlement', () => {
    const rows = [row({ id: 'old', settlementDueDate: '2026-01-01' }), row({ id: 'new', settlementDueDate: '2026-12-01' })];
    expect(settlementFor(rows, 'o1')?.id).toBe('new');
  });
  test('only Paid clears the item', () => {
    expect(settlementStage(null)).toBe('none');
    expect(settlementStage(row({ status: 'PendingApproval' }))).toBe('pending');
    expect(isSettlementCleared(row({ status: 'Approved' }))).toBe(false);
    expect(isSettlementCleared(row({ status: 'Paid' }))).toBe(true);
  });
});
