import { expect, test } from '@playwright/test';
import { isRunDutyOpen, runDutySummary, settlementRunHref } from '../src/lib/offboardingSettlement';

test.describe('final settlement run duty (G-006)', () => {
  test('an approved settlement with no run is a job waiting for Payroll; anything else is not', () => {
    expect(isRunDutyOpen({ status: 'Approved', linkedRunId: null, payrollRunId: null })).toBe(true);
    expect(isRunDutyOpen({ status: 'Approved', linkedRunId: 'r1', payrollRunId: null })).toBe(false);
    expect(isRunDutyOpen({ status: 'Approved', linkedRunId: null, payrollRunId: 'r1' })).toBe(false);
    for (const status of ['Draft', 'PendingApproval', 'Disbursing', 'Paid', 'Cancelled'])
      expect(isRunDutyOpen({ status })).toBe(false);
    // The server's own answer wins when it sends one.
    expect(isRunDutyOpen({ status: 'Approved', runDutyOpen: false })).toBe(false);
  });

  test('the link opens the Payroll runs tab on that run', () => {
    expect(settlementRunHref('5f0c7e3a-1111-4222-8333-444455556666')).toBe('/payroll?tab=runs&run=5f0c7e3a-1111-4222-8333-444455556666');
  });

  test('the dashboard card counts the settlements still needing a run and names the earliest due date', () => {
    const rows = [
      { status: 'Approved', settlementDueDate: '2026-11-20', runDutyOpen: true },
      { status: 'Approved', settlementDueDate: '2026-11-12', runDutyOpen: true },
      { status: 'Approved', settlementDueDate: '2026-11-01', runDutyOpen: false, linkedRunId: 'r1' },
      { status: 'Paid', settlementDueDate: '2026-10-01' },
    ];
    expect(runDutySummary(rows)).toEqual({ count: 2, nextDue: '2026-11-12' });
    expect(runDutySummary([])).toEqual({ count: 0, nextDue: null });
    expect(runDutySummary(rows.slice(2))).toEqual({ count: 0, nextDue: null });
  });
});
