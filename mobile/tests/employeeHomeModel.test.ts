import test from 'node:test';
import assert from 'node:assert/strict';
import {
  buildNeedsYou,
  classifyDay,
  displayFirstName,
  documentKind,
  greetingPeriod,
  isRecentPeriod,
  minutesBetween,
  nextHoliday,
  pickAnnualBalance,
  restDayFromHistory,
  splitMinutes,
  summarizePending,
  holidayOn,
} from '../src/features/dashboard/employeeHomeModel.ts';
import type { DayRecord, PublicHolidayItem } from '../src/api/homeMappers.ts';

// ---- display name -------------------------------------------------------------------------

test('greets by the whole first given name, keeping compound names together', () => {
  assert.equal(displayFirstName('Mohammed Abdelrahman'), 'Mohammed');
  assert.equal(displayFirstName('Abdul Rahman Al-Harbi'), 'Abdul Rahman');
  assert.equal(displayFirstName('عبد الله الحربي'), 'عبد الله');
  assert.equal(displayFirstName('Abd Al Aziz Al-Saud'), 'Abd Al Aziz');
  assert.equal(displayFirstName('Abu Bakr Salem'), 'Abu Bakr');
  assert.equal(displayFirstName('Saif al-Din Khalid'), 'Saif al-Din');
  assert.equal(displayFirstName('Nour El Din Hassan'), 'Nour El Din');
  assert.equal(displayFirstName('نور الدين حسن'), 'نور الدين');
  assert.equal(displayFirstName('Abdulrahman Al-Qahtani'), 'Abdulrahman');
  assert.equal(displayFirstName('محمد عبدالرحمن'), 'محمد');
});

test('a preferred name wins; an empty name stays empty (no "there")', () => {
  assert.equal(displayFirstName('Abdul Rahman Al-Harbi', 'Abood'), 'Abood');
  assert.equal(displayFirstName('  ', '  '), '');
  assert.equal(displayFirstName(undefined), '');
});

// ---- greeting on the company clock ---------------------------------------------------------

test('greeting uses the Riyadh hour, not the phone hour', () => {
  // 05:30Z = 08:30 Riyadh → morning, even though it is 01:30 in New York.
  assert.equal(greetingPeriod(new Date('2026-10-10T05:30:00Z'), 'Asia/Riyadh'), 'morning');
  assert.equal(greetingPeriod(new Date('2026-10-10T10:00:00Z'), 'Asia/Riyadh'), 'afternoon');
  assert.equal(greetingPeriod(new Date('2026-10-10T15:00:00Z'), 'Asia/Riyadh'), 'evening');
  assert.equal(greetingPeriod(new Date('2026-10-10T22:00:00Z'), 'Asia/Riyadh'), 'evening');
});

// ---- day state: never "Absent" from missing punches ---------------------------------------

const rest = (date: string): DayRecord => ({ date, status: 'WEEKEND', rawStatus: 'Rest day', missingPunch: false, correctionRequested: false, payrollLocked: false });
const present = (date: string): DayRecord => ({ date, status: 'PRESENT', rawStatus: 'Present', missingPunch: false, correctionRequested: false, payrollLocked: false });

test('no punches and no processed record is "not started", never absent', () => {
  const state = classifyDay({ currentlyActive: false });
  assert.equal(state.kind, 'notStarted');
  assert.equal(state.tone, 'neutral');
  assert.equal(state.punchExpected, true);
});

test('only a processed record can say absent', () => {
  assert.equal(classifyDay({ currentlyActive: false, processedStatus: 'ABSENT' }).kind, 'absent');
  assert.equal(classifyDay({ currentlyActive: false, processedStatus: 'ABSENT' }).tone, 'danger');
});

test('a Saturday is a rest day when recent Saturdays were processed as rest days', () => {
  // 2026-10-10 is a Saturday; 3 and 26 Sep / 3 Oct were rest days.
  const records = [rest('2026-10-03'), rest('2026-09-26'), present('2026-10-08'), rest('2026-10-02')];
  const byHistory = restDayFromHistory('2026-10-10', records);
  assert.equal(byHistory, true);
  const state = classifyDay({ currentlyActive: false, restDayByHistory: byHistory });
  assert.equal(state.kind, 'restDay');
  assert.equal(state.tone, 'neutral');
  assert.equal(state.punchExpected, false);
});

test('rest-day history needs a processed rest day on this weekday and none worked', () => {
  // The demo tenant today: one processed Saturday (3 Oct), a rest day.
  assert.equal(restDayFromHistory('2026-10-10', [rest('2026-10-03'), rest('2026-10-02'), present('2026-10-01')]), true);
  assert.equal(restDayFromHistory('2026-10-10', []), false);
  assert.equal(restDayFromHistory('2026-10-10', [rest('2026-10-03'), present('2026-09-26')]), false);
  // A Sunday (working day) with Saturdays as rest days is not a rest day.
  assert.equal(restDayFromHistory('2026-10-11', [rest('2026-10-03'), rest('2026-09-26')]), false);
});

test('approved leave and public holidays show their own neutral states', () => {
  const leave = classifyDay({ currentlyActive: false, approvedLeaveToday: { leaveTypeName: 'Annual Leave' } });
  assert.equal(leave.kind, 'onLeave');
  assert.equal(leave.leaveTypeName, 'Annual Leave');
  const holiday = classifyDay({ currentlyActive: false, holidayToday: { nameEn: 'Saudi National Day', nameAr: 'اليوم الوطني' }, language: 'ar' });
  assert.equal(holiday.kind, 'holiday');
  assert.equal(holiday.holidayName, 'اليوم الوطني');
  // Processed records outrank the client's evidence.
  assert.equal(classifyDay({ currentlyActive: false, processedStatus: 'WEEKEND', approvedLeaveToday: { leaveTypeName: 'Annual Leave' } }).kind, 'restDay');
});

test('punches decide first: on the clock, then day complete', () => {
  assert.equal(classifyDay({ currentlyActive: true, clockIn: '2026-10-08T05:00:00Z', restDayByHistory: true }).kind, 'onClock');
  const done = classifyDay({ currentlyActive: false, clockIn: '2026-10-08T05:00:00Z', clockOut: '2026-10-08T14:00:00Z', processedStatus: 'LATE' });
  assert.equal(done.kind, 'done');
  assert.equal(done.tone, 'warning');
});

// ---- durations ------------------------------------------------------------------------------

test('worked minutes between instants; offset-less API times are UTC', () => {
  assert.equal(minutesBetween('2026-10-08T05:00:00Z', '2026-10-08T13:12:00Z'), 492);
  assert.equal(minutesBetween('2026-10-08T05:00:00', '2026-10-08T06:30:00'), 90);
  assert.equal(minutesBetween('2026-10-08T06:00:00Z', '2026-10-08T05:00:00Z'), null);
  assert.equal(minutesBetween(undefined, '2026-10-08T05:00:00Z'), null);
  assert.deepEqual(splitMinutes(492), { h: 8, m: 12 });
});

// ---- KPIs -----------------------------------------------------------------------------------

test('annual balance is found by name, never guessed', () => {
  const annual = { leaveTypeId: 'a', leaveTypeName: 'Annual Leave', allocated: 21, used: 6, pending: 4, available: 11, unit: 'DAYS' as const };
  const sick = { ...annual, leaveTypeId: 's', leaveTypeName: 'Sick Leave' };
  assert.equal(pickAnnualBalance([sick, annual])?.leaveTypeId, 'a');
  assert.equal(pickAnnualBalance([sick]), null);
  assert.equal(pickAnnualBalance([]), null);
});

test('pending combines leave, overtime and HR; a failed list is unknown, not zero', () => {
  assert.deepEqual(
    summarizePending([{ source: 'leave', count: 1 }, { source: 'overtime', count: 1 }, { source: 'hr', count: 2 }]).total,
    4,
  );
  const partial = summarizePending([{ source: 'leave', count: 1 }, { source: 'overtime', count: null }, { source: 'hr', count: 2 }]);
  assert.equal(partial.total, 3);
  assert.equal(partial.partial, true);
  const none = summarizePending([{ source: 'leave', count: null }, { source: 'overtime', count: null }, { source: 'hr', count: null }]);
  assert.equal(none.total, null);
});

// ---- Needs you --------------------------------------------------------------------------------

test('needs-you: expired documents, then missing punches, then expiring documents, then a new payslip; max 3', () => {
  const items = buildNeedsYou({
    today: '2026-10-10',
    documents: [
      { id: 'p', documentType: 'Passport', expiryDate: '2026-11-20' },
      { id: 'i', documentType: 'Iqama', expiryDate: '2026-10-01' },
    ],
    dayRecords: [
      { date: '2026-10-07', status: 'MISSING_PUNCH', rawStatus: 'Present', missingPunch: true, correctionRequested: false, payrollLocked: false },
      { date: '2026-10-06', status: 'MISSING_PUNCH', rawStatus: 'Present', missingPunch: true, correctionRequested: true, payrollLocked: false },
    ],
    latestPayslip: { id: 'ps', year: 2026, month: 9, periodLabel: 'September 2026' },
  });
  assert.deepEqual(items.map((i) => i.kind), ['documentExpired', 'missingPunch', 'documentExpiring']);
  assert.equal(items[1].kind === 'missingPunch' && items[1].date, '2026-10-07');
});

test('needs-you: a seen or old payslip is not news; nothing pads an empty list', () => {
  const p = { id: 'ps', year: 2026, month: 9, periodLabel: 'September 2026' };
  assert.equal(buildNeedsYou({ today: '2026-10-10', latestPayslip: p }).length, 1);
  assert.equal(buildNeedsYou({ today: '2026-10-10', latestPayslip: p, seenPayslipId: 'ps' }).length, 0);
  assert.equal(isRecentPeriod('2026-10-10', 2026, 7), false);
  assert.deepEqual(buildNeedsYou({ today: '2026-10-10' }), []);
});

test('document types map to translatable kinds, unknown types stay generic', () => {
  assert.equal(documentKind('Iqama'), 'iqama');
  assert.equal(documentKind('Residence Permit'), 'iqama');
  assert.equal(documentKind('جواز سفر'), 'passport');
  assert.equal(documentKind('Training certificate'), 'document');
});

// ---- holidays -------------------------------------------------------------------------------

test('next public holiday skips optional and past days', () => {
  const h = (date: string, isOptional = false): PublicHolidayItem => ({ id: date, date, nameEn: date, isOptional });
  const list = [h('2026-09-23'), h('2026-12-01', true), h('2027-02-22'), h('2026-10-10')];
  assert.equal(nextHoliday(list, '2026-10-10')?.date, '2026-10-10');
  assert.equal(nextHoliday(list, '2026-10-11')?.date, '2027-02-22');
  assert.equal(holidayOn(list, '2026-12-01'), null);
  assert.equal(nextHoliday([], '2026-10-10'), null);
});
