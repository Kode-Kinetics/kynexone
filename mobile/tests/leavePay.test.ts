import test from 'node:test';
import assert from 'node:assert/strict';
import {
  addIsoDays,
  balanceHint,
  calendarDaysInclusive,
  describeRequestedLength,
  leaveActionFor,
  leaveGroupFor,
  leaveStatusKind,
  leaveStatusTone,
  nextApproverFrom,
  normalizeLeaveRange,
} from '../src/features/leave/leaveLogic.ts';
import {
  applyRounding,
  checkOvertime,
  MAX_OVERTIME_MINUTES,
  overtimeDurationMinutes,
  overtimeStatusKind,
  splitMinutes,
  zonedWallTimeToUtcIso,
} from '../src/features/overtime/overtimeLogic.ts';
import {
  adjacentPayslips,
  deductionCategory,
  monthBounds,
  payslipCheck,
  payslipStatusKind,
  unitemisedAmount,
} from '../src/features/payslips/payslipLogic.ts';
import { mapLeaveBalanceRow, simpleBalanceEquation } from '../src/api/leaveBalanceMapper.ts';
import { classifyLeavePayError, errorShape, pluralCategory } from '../src/features/leave/leavePayText.ts';

// ---- Leave ---------------------------------------------------------------------------------

test('half day forces a single date, whatever end date was picked before', () => {
  // The old screen sent isHalfDay with 16–19 Nov when Half day was switched on after the range.
  assert.deepEqual(normalizeLeaveRange({ start: '2026-11-16', end: '2026-11-19', mode: 'half' }), {
    start: '2026-11-16',
    end: '2026-11-16',
  });
  assert.deepEqual(describeRequestedLength({ start: '2026-11-16', end: '2026-11-19', mode: 'half' }), {
    basis: 'halfDay',
    days: 0.5,
  });
});

test('a full-day range keeps its end; an end before the start snaps to the start', () => {
  assert.deepEqual(normalizeLeaveRange({ start: '2026-11-16', end: '2026-11-19', mode: 'full' }), {
    start: '2026-11-16',
    end: '2026-11-19',
  });
  assert.deepEqual(normalizeLeaveRange({ start: '2026-11-16', end: '2026-11-10', mode: 'full' }), {
    start: '2026-11-16',
    end: '2026-11-16',
  });
});

test('length before submit is calendar days (inclusive), never claimed as working days', () => {
  assert.equal(calendarDaysInclusive('2026-11-16', '2026-11-19'), 4);
  assert.equal(calendarDaysInclusive('2026-12-31', '2027-01-01'), 2);
  assert.equal(calendarDaysInclusive('2026-11-19', '2026-11-16'), 0);
  assert.deepEqual(describeRequestedLength({ start: '2026-11-15', end: '2026-11-21', mode: 'full' }), {
    basis: 'calendar',
    days: 7,
  });
  assert.equal(addIsoDays('2026-02-28', 1), '2026-03-01');
});

test('balance hint never asserts "over balance" from a calendar-day guess', () => {
  assert.equal(balanceHint(4, 11), 'within');
  assert.equal(balanceHint(14, 11), 'mayExceed');
  assert.equal(balanceHint(4, undefined), 'unknown');
  assert.equal(balanceHint(0.5, 0), 'mayExceed');
});

test('leave status mapping covers every backend status', () => {
  assert.equal(leaveStatusKind('Submitted'), 'pending');
  assert.equal(leaveStatusKind('PendingManagerApproval'), 'pending');
  assert.equal(leaveStatusKind('PendingHRApproval'), 'pending');
  assert.equal(leaveStatusKind('Approved'), 'approved');
  assert.equal(leaveStatusKind('Rejected'), 'rejected');
  assert.equal(leaveStatusKind('Cancelled'), 'cancelled');
  assert.equal(leaveStatusKind('Withdrawn'), 'withdrawn');
  assert.equal(leaveStatusKind('CancellationRequested'), 'cancellationPending');
  assert.equal(leaveStatusKind('Draft'), 'draft');
  assert.equal(leaveStatusKind(''), 'unknown');
  assert.equal(leaveStatusTone('approved'), 'success');
  assert.equal(leaveStatusTone('cancellationPending'), 'warning');
  assert.equal(leaveStatusTone('rejected'), 'danger');
});

test('withdraw while pending; ask to cancel only approved leave that has not started', () => {
  const today = '2026-10-10';
  assert.equal(leaveActionFor('pending', '2026-11-16', today), 'withdraw');
  assert.equal(leaveActionFor('approved', '2026-10-19', today), 'requestCancel');
  assert.equal(leaveActionFor('approved', '2026-10-10', today), null);
  assert.equal(leaveActionFor('approved', '2026-09-01', today), null);
  assert.equal(leaveActionFor('cancellationPending', '2026-11-16', today), null);
  assert.equal(leaveActionFor('rejected', '2026-11-16', today), null);
  assert.equal(leaveGroupFor('pending', '2026-11-19', today), 'inProgress');
  assert.equal(leaveGroupFor('approved', '2026-10-20', today), 'upcoming');
  assert.equal(leaveGroupFor('approved', '2026-10-01', today), 'past');
  assert.equal(leaveGroupFor('cancelled', '2026-11-19', today), 'past');
});

test('next approver comes from the first pending step', () => {
  assert.deepEqual(
    nextApproverFrom([
      { stepNumber: 2, approverRole: 'HR', approverName: '', decision: 'Pending' },
      { stepNumber: 1, approverRole: 'Manager', approverName: 'Majed', decision: 'Approved' },
    ]),
    { kind: 'hr' },
  );
  assert.deepEqual(nextApproverFrom([{ stepNumber: 1, approverRole: 'Manager', decision: 'Pending' }]), { kind: 'manager' });
  assert.deepEqual(nextApproverFrom([{ stepNumber: 1, approverRole: 'HR Manager', decision: 'Pending' }]), { kind: 'hr' });
  assert.deepEqual(nextApproverFrom([{ stepNumber: 1, approverName: 'Nouf Alharbi', decision: 'Pending' }]), {
    kind: 'named',
    name: 'Nouf Alharbi',
  });
  assert.equal(nextApproverFrom([{ stepNumber: 1, approverRole: 'HR', decision: 'Approved' }]), null);
});

// ---- Overtime ------------------------------------------------------------------------------

test('overtime duration: same day, explicit next day, and no silent wrap', () => {
  assert.equal(overtimeDurationMinutes('17:00', '19:30', false), 150);
  // 22:00 → 02:00 is only overnight when the user says so.
  assert.equal(overtimeDurationMinutes('22:00', '02:00', false), null);
  assert.equal(overtimeDurationMinutes('22:00', '02:00', true), 240);
  // The old typo case: 09:00 → 08:00 used to become 23 hours.
  assert.equal(overtimeDurationMinutes('09:00', '08:00', false), null);
  assert.equal(checkOvertime({ start: '09:00', end: '08:00', endsNextDay: false }).issue, 'endNotAfterStart');
});

test('overtime is capped at 12 hours, including across midnight', () => {
  assert.equal(MAX_OVERTIME_MINUTES, 720);
  assert.equal(checkOvertime({ start: '08:00', end: '20:00', endsNextDay: false }).issue, null);
  assert.equal(checkOvertime({ start: '08:00', end: '20:15', endsNextDay: false }).issue, 'overHardCap');
  assert.equal(checkOvertime({ start: '09:00', end: '08:00', endsNextDay: true }).issue, 'overHardCap');
  assert.equal(checkOvertime({ start: '20:00', end: '08:00', endsNextDay: true }).issue, null);
  assert.equal(checkOvertime({ start: null, end: '08:00', endsNextDay: false }).issue, 'missingTimes');
});

test('overtime policy limits mirror the server rounding and limits', () => {
  const policy = { minimumMinutes: 30, maximumMinutesPerDay: 240, roundingRule: 'Nearest15' };
  assert.equal(applyRounding(157, 'Nearest15'), 150);
  assert.equal(applyRounding(158, 'Nearest15'), 165);
  assert.equal(applyRounding(10, 'Up15'), 15);
  assert.equal(applyRounding(44, 'Down15'), 30);
  assert.equal(applyRounding(47, 'unknown'), 47);
  assert.deepEqual(checkOvertime({ start: '17:00', end: '17:20', endsNextDay: false, policy }), {
    minutes: 20,
    roundedMinutes: 15,
    issue: 'belowPolicyMinimum',
  });
  assert.equal(checkOvertime({ start: '17:00', end: '22:00', endsNextDay: false, policy }).issue, 'abovePolicyDailyMax');
  assert.equal(checkOvertime({ start: '17:00', end: '21:00', endsNextDay: false, policy }).issue, null);
  assert.deepEqual(splitMinutes(150), { h: 2, m: 30 });
});

test('overtime wall times are sent on the company clock, not the device clock', () => {
  assert.equal(zonedWallTimeToUtcIso('2026-10-07', '17:00', 'Asia/Riyadh'), '2026-10-07T14:00:00.000Z');
  assert.equal(zonedWallTimeToUtcIso('2026-10-07', '02:00', 'Asia/Riyadh', 1), '2026-10-07T23:00:00.000Z');
  assert.equal(zonedWallTimeToUtcIso('2026-10-07', '09:00', 'Asia/Dubai'), '2026-10-07T05:00:00.000Z');
});

test('overtime status mapping', () => {
  assert.equal(overtimeStatusKind('PendingManager'), 'waitingManager');
  assert.equal(overtimeStatusKind('PendingHR'), 'waitingHr');
  assert.equal(overtimeStatusKind('Approved'), 'approved');
  assert.equal(overtimeStatusKind('Rejected'), 'rejected');
  assert.equal(overtimeStatusKind('Cancelled'), 'cancelled');
  assert.equal(overtimeStatusKind(undefined), 'unknown');
});

// ---- Payslips ------------------------------------------------------------------------------

test('payslip check line: Gross − Deductions = Net, a mismatch is reported, not hidden', () => {
  assert.deepEqual(payslipCheck(10800, 0, 10800), { expectedNet: 10800, matches: true, difference: 0 });
  assert.deepEqual(payslipCheck(12500, 1312.5, 11187.5), { expectedNet: 11187.5, matches: true, difference: 0 });
  assert.deepEqual(payslipCheck(12500, 1312.5, 11200), { expectedNet: 11187.5, matches: false, difference: 12.5 });
  assert.equal(payslipCheck(0.1 + 0.2, 0.3, 0).matches, true);
});

test('deductions the lines do not explain are flagged, never turned into a line', () => {
  assert.equal(unitemisedAmount(1500, [{ amount: 975 }, { amount: 500 }]), 25);
  assert.equal(unitemisedAmount(1475, [{ amount: 975 }, { amount: 500 }]), 0);
  assert.equal(deductionCategory('EmployerLoan'), 'EmployerLoan');
  assert.equal(deductionCategory('SomethingNew'), 'Other');
});

test('payslip month switcher and period bounds', () => {
  const list = [{ id: 'sep' }, { id: 'aug' }, { id: 'jul' }];
  assert.deepEqual(adjacentPayslips(list, 'aug'), { newer: { id: 'sep' }, older: { id: 'jul' } });
  assert.deepEqual(adjacentPayslips(list, 'sep'), { newer: null, older: { id: 'aug' } });
  assert.deepEqual(adjacentPayslips(list, 'x'), { newer: null, older: null });
  assert.deepEqual(monthBounds(2026, 2), { first: '2026-02-01', last: '2026-02-28' });
  assert.equal(monthBounds(0, 2), null);
  assert.equal(payslipStatusKind('Final'), 'final');
  assert.equal(payslipStatusKind('PROCESSED'), 'final');
  assert.equal(payslipStatusKind('Paid'), 'paid');
});

// ---- Text ----------------------------------------------------------------------------------

test('Arabic plural categories', () => {
  assert.deepEqual([0, 1, 2, 3, 10, 11, 99, 100, 102, 103, 0.5].map((n) => pluralCategory('ar', n)), [
    'zero', 'one', 'two', 'few', 'few', 'many', 'many', 'other', 'other', 'few', 'other',
  ]);
  assert.deepEqual([0, 1, 2, 0.5].map((n) => pluralCategory('en', n)), ['other', 'one', 'other', 'other']);
});

test('server refusals map to plain-language message kinds', () => {
  assert.equal(classifyLeavePayError({ network: true }), 'offline');
  assert.equal(
    classifyLeavePayError({ status: 400, message: 'Employee already has an approved or pending leave for the requested dates.' }),
    'overlap',
  );
  assert.equal(classifyLeavePayError({ status: 400, message: 'Insufficient leave balance for 2026.' }), 'insufficientBalance');
  assert.equal(classifyLeavePayError({ status: 400, message: 'Maximum overtime is 240 minutes per day.' }), 'overtimeAboveDailyMax');
  assert.equal(classifyLeavePayError({ status: 403, code: 'feature_not_enabled' }), 'featureOff');
  assert.equal(classifyLeavePayError({ status: 422, code: 'no_workflow' }), 'noApprovalRoute');
  assert.equal(classifyLeavePayError({ status: 500 }), 'generic');
  assert.deepEqual(errorShape({ response: { status: 409, data: { message: 'x' } } }), { status: 409, message: 'x', code: undefined });
  assert.deepEqual(errorShape({ request: {}, message: 'Network Error' }), { network: true, message: 'Network Error' });
});

// ---- Leave balance mapping (real responses, Mohammed @ masar, 2026-10-10) ------------------


const BALANCES_EMPLOYEE_3 = JSON.parse(`[{"id":"3156881e-2574-4c23-9018-4593b4860851","tenantId":"878bfe61-64fc-4894-bad9-72afaa6f223c","employeeId":3,"employeeName":"Mohammed Abdelrahman","leaveTypeId":"5a95b640-5ad6-4fb4-9ffe-24dcb7b44a7f","leaveTypeName":"Annual Leave","year":2026,"entitled":21.00,"accrued":12.25,"used":6.00,"pending":4.00,"carriedForward":0.00,"encashed":0.00,"expired":0.00,"manualAdjustment":0.00,"negativeAllowed":false,"createdAtUtc":"2026-10-10T17:38:23.053597Z","updatedAtUtc":"2026-10-10T17:45:35.042127Z","granted":21.00,"statutoryEntitlementDays":null,"available":11.00}]`);
const ESS_LEAVE_BALANCE = JSON.parse(`[{"leaveTypeId":"5a95b640-5ad6-4fb4-9ffe-24dcb7b44a7f","leaveTypeName":"Annual Leave","entitled":21.00,"used":6.00,"pending":4.00,"available":11.00,"statutoryEntitlementDays":null}]`);

test('leave balance: every figure is the server field, pending is not subtracted twice', () => {
  const row = mapLeaveBalanceRow(BALANCES_EMPLOYEE_3[0]);
  assert.equal(row.available, 11);
  assert.equal(row.used, 6);
  assert.equal(row.pending, 4);
  assert.equal(row.granted, 21);
  assert.equal(row.year, 2026);
  assert.equal(row.statutoryDays, null);
  assert.deepEqual(simpleBalanceEquation(row), { granted: 21, used: 6, pending: 4, available: 11 });
});

test('leave balance: GET /ess/leave/balance maps to the same numbers', () => {
  const row = mapLeaveBalanceRow(ESS_LEAVE_BALANCE[0]);
  const admin = mapLeaveBalanceRow(BALANCES_EMPLOYEE_3[0]);
  assert.deepEqual(
    [row.available, row.used, row.pending, row.granted],
    [admin.available, admin.used, admin.pending, admin.granted],
  );
  assert.equal(row.year, null);
});

test('leave balance: no equation when carry-forward or adjustments are in play', () => {
  const row = mapLeaveBalanceRow({ ...BALANCES_EMPLOYEE_3[0], carriedForward: 5, available: 16 });
  assert.equal(row.available, 16);
  assert.equal(simpleBalanceEquation(row), null);
  assert.equal(simpleBalanceEquation(mapLeaveBalanceRow({ ...BALANCES_EMPLOYEE_3[0], available: 12 })), null);
});
