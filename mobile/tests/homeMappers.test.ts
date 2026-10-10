import test from 'node:test';
import assert from 'node:assert/strict';
import {
  approvedLeaveOn,
  isPendingStatus,
  mapAnnouncements,
  mapAttendanceStatus,
  mapDashboardExtras,
  mapDayRecord,
  mapHolidays,
} from '../src/api/homeMappers.ts';
import { notificationTarget } from '../src/features/notifications/notificationTarget.ts';
import { companyWallTimeToUtcIso, validateCorrection } from '../src/features/attendance/companyTime.ts';

test('dashboard announcements are kept (the old mapper dropped them)', () => {
  const extras = mapDashboardExtras({
    announcements: [
      { id: 'a1', title: 'Ramadan working hours', body: '9:00 to 15:00', audience: 'All', publishedAtUtc: '2026-10-01T06:00:00Z' },
      { id: '', title: 'no id' },
      { id: 'a3', title: '   ' },
    ],
    attendanceToday: null,
  });
  assert.equal(extras.announcements.length, 1);
  assert.equal(extras.announcements[0].title, 'Ramadan working hours');
  assert.equal(extras.todayProcessedStatus, null);
  assert.equal(extras.todayWorkedMinutes, null);
  assert.deepEqual(mapAnnouncements(null), []);
});

test("today's status counts only when processed, not when derived from raw punches", () => {
  const raw = mapDashboardExtras({ attendanceToday: { status: 'Present', totalWorkedMinutes: 120 }, attendanceTodaySource: 'raw' });
  assert.equal(raw.todayProcessedStatus, null);
  assert.equal(raw.todayWorkedMinutes, 120);
  const processed = mapDashboardExtras({ attendanceToday: { status: 'Rest day', totalWorkedMinutes: 0 }, attendanceTodaySource: 'processed' });
  assert.equal(processed.todayProcessedStatus, 'WEEKEND');
  assert.equal(processed.todayWorkedMinutes, null);
});

test('unknown attendance statuses are unknown, not absent', () => {
  assert.equal(mapAttendanceStatus('Rest day'), 'WEEKEND');
  assert.equal(mapAttendanceStatus('Public holiday'), 'HOLIDAY');
  assert.equal(mapAttendanceStatus('On leave'), 'ON_LEAVE');
  assert.equal(mapAttendanceStatus('Something new'), null);
  assert.equal(mapAttendanceStatus(undefined), null);
});

test('a missing punch is one punch without the other; no punches keeps the server status', () => {
  const base = { workDate: '2026-10-07', status: 'Present', manualCorrectionStatus: 'None', isPayrollLocked: false };
  assert.equal(mapDayRecord({ ...base, missingPunch: true, firstInUtc: '2026-10-07T05:00:00Z', lastOutUtc: null })?.status, 'MISSING_PUNCH');
  assert.equal(mapDayRecord({ ...base, status: 'Absent', missingPunch: true, firstInUtc: null, lastOutUtc: null })?.status, 'ABSENT');
  assert.equal(mapDayRecord({ ...base, manualCorrectionStatus: 'Pending' })?.correctionRequested, true);
  assert.equal(mapDayRecord({ workDate: 'bad' }), null);
});

test('holidays: valid rows only, sorted, Arabic name kept', () => {
  const list = mapHolidays([
    { id: '2', nameEn: 'Founding Day', nameAr: 'يوم التأسيس', date: '2027-02-22', isOptional: false },
    { id: '1', nameEn: 'Saudi National Day', nameAr: 'اليوم الوطني', date: '2026-09-23T00:00:00', isOptional: false },
    { id: 'x', nameEn: '', nameAr: '', date: '2026-01-01' },
  ]);
  assert.deepEqual(list.map((h) => h.date), ['2026-09-23', '2027-02-22']);
  assert.equal(list[1].nameAr, 'يوم التأسيس');
  assert.deepEqual(mapHolidays({}), []);
});

test('pending means still waiting on someone, per source', () => {
  assert.equal(isPendingStatus('leave', 'PendingHRApproval'), true);
  assert.equal(isPendingStatus('leave', 'Approved'), false);
  assert.equal(isPendingStatus('leave', 'Cancelled'), false);
  assert.equal(isPendingStatus('overtime', 'PendingManager'), true);
  assert.equal(isPendingStatus('overtime', 'Rejected'), false);
  assert.equal(isPendingStatus('hr', 'Open'), true);
  assert.equal(isPendingStatus('hr', 'In Progress'), true);
  assert.equal(isPendingStatus('hr', 'Resolved'), false);
  assert.equal(isPendingStatus('hr', 'Closed'), false);
});

test('approved leave covering today', () => {
  const rows = [
    { status: 'Approved', leaveTypeName: 'Annual Leave', startDate: '2026-10-19', endDate: '2026-10-20' },
    { status: 'PendingHRApproval', leaveTypeName: 'Annual Leave', startDate: '2026-10-10', endDate: '2026-10-12' },
  ];
  assert.deepEqual(approvedLeaveOn(rows, '2026-10-20'), { leaveTypeName: 'Annual Leave' });
  assert.equal(approvedLeaveOn(rows, '2026-10-10'), null);
  assert.equal(approvedLeaveOn(null, '2026-10-10'), null);
});

test('notifications open the matching record, or nothing', () => {
  assert.deepEqual(notificationTarget('Leave approved', 'Your Annual Leave request was approved'), { route: 'ApplyLeave', params: { segment: 'history' } });
  assert.deepEqual(notificationTarget('Your payslip for 2026/09 is ready', ''), { route: 'Payslips' });
  assert.deepEqual(notificationTarget('HR replied to your request', ''), { route: 'HRRequests' });
  assert.deepEqual(notificationTarget('Your document is ready', ''), { route: 'Letters' });
  assert.deepEqual(notificationTarget('تمت الموافقة على طلب الإجازة', ''), { route: 'ApplyLeave', params: { segment: 'history' } });
  assert.equal(notificationTarget('Welcome to KynexOne', 'Glad to have you'), null);
  assert.equal(notificationTarget('', ''), null);
});

test('a corrected punch time is company (Riyadh) time, whatever the phone zone', () => {
  assert.equal(companyWallTimeToUtcIso('2026-10-08', '08:00'), '2026-10-08T05:00:00.000Z');
  assert.equal(companyWallTimeToUtcIso('2026-10-08', '00:30'), '2026-10-07T21:30:00.000Z');
  assert.equal(companyWallTimeToUtcIso('2026-10-08', '08:00', 'Asia/Dubai'), '2026-10-08T04:00:00.000Z');
  assert.throws(() => companyWallTimeToUtcIso('2026-10-08', '25:00'));
});


test('a correction needs a day, one time, a real reason, and out after in', () => {
  assert.deepEqual(validateCorrection({ date: '2026-10-08', clockIn: '08:00', clockOut: null, reason: 'Forgot to punch' }), {});
  assert.deepEqual(validateCorrection({ date: null, clockIn: null, clockOut: null, reason: 'x' }), {
    date: 'dateRequired', times: 'timeRequired', reason: 'reasonShort',
  });
  assert.equal(validateCorrection({ date: '2026-10-08', clockIn: '17:00', clockOut: '08:00', reason: 'Wrong order' }).times, 'orderInvalid');
});
