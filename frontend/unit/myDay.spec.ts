import { expect, test } from '@playwright/test';
import type { EssDashboard, EssHrRequest } from '../src/api/ess';
import type { LeaveRequest } from '../src/api/leave';
import {
  advanceItem, buildNextStep, loanItem, clockParts, daysBetween, deriveDay, hrRequestItem, leaveItem, liveWorkedSeconds, orderTracker, pickUpcoming, whenLabel,
  type MyDayFormat, type UpcomingEntry,
} from '../src/lib/myDay';

// Keys stand in for sentences; the placeholders are filled so assertions can read the result.
const t = (key: string, params?: Record<string, unknown>) => key.replace(/\{(\w+)\}/g, (_, k: string) => String(params?.[k] ?? `{${k}}`));
const fx: MyDayFormat = {
  date: (d) => String(d).slice(0, 10), dateTime: (d) => String(d).slice(0, 16), time: (d) => String(d).slice(11, 16), number: (n) => String(n),
};
const NOW = Date.parse('2026-11-15T12:10:07Z');
const ago = (ms: number) => new Date(NOW - ms).toISOString();
type Att = NonNullable<EssDashboard['attendanceToday']>;
const att = (over: Partial<Att> = {}): Att => ({ workDate: '2026-11-15', status: 'Present', totalWorkedMinutes: 0, missingPunch: false, lateMinutes: 0, overtimeMinutes: 0, ...over });

test('hours are live only when the punch times make that exact', () => {
  const open = att({ firstInUtc: ago((4 * 60 + 12) * 60_000 + 7000) });
  expect(liveWorkedSeconds(open, NOW)).toBe(4 * 3600 + 12 * 60 + 7);
  // Closed time already counted: the open period's start is not in the payload, so no live figure.
  expect(liveWorkedSeconds({ ...open, totalWorkedMinutes: 90 }, NOW)).toBeNull();
  expect(liveWorkedSeconds({ ...open, lastOutUtc: ago(1000) }, NOW)).toBeNull();
  expect(liveWorkedSeconds({ ...open, missingPunch: true }, NOW)).toBeNull();
  expect(liveWorkedSeconds(att(), NOW)).toBeNull();
  expect(liveWorkedSeconds(null, NOW)).toBeNull();
  // A punch from yesterday is not a running clock; a clock a little behind the server reads zero, never negative.
  expect(liveWorkedSeconds(att({ firstInUtc: ago(20 * 3600_000) }), NOW)).toBeNull();
  expect(liveWorkedSeconds(att({ firstInUtc: new Date(NOW + 3000).toISOString() }), NOW)).toBe(0);
  expect(clockParts(4 * 3600 + 12 * 60 + 7)).toEqual({ hm: '4:12', ss: '07' });
});

test('whole days read as sentences', () => {
  expect(daysBetween('2026-11-15', '2026-11-26')).toBe(11);
  expect(daysBetween('2026-11-15', '2026-11-10')).toBe(-5);
  expect(daysBetween('2026-02-28', '2026-03-01')).toBe(1);
  expect(whenLabel(t, 0)).toBe('Today');
  expect(whenLabel(t, 1)).toBe('Tomorrow');
  expect(whenLabel(t, 11)).toContain('in #');
});

const dash = (over: Partial<EssDashboard> = {}): EssDashboard => ({
  profile: { employeeId: 7, employeeCode: 'EMP-0007', fullName: 'Noura Alqahtani', jobTitle: '', department: '', profilePhotoUrl: '', profileCompletenessScore: 80 },
  attendanceToday: null, leaveBalances: [], pendingRequests: 0, documentAlerts: [], announcements: [], notifications: [], actionItems: [],
  payrollSnapshot: null, loansSummary: null, performanceSnapshot: null, overtimeHoursThisMonth: 0, nextApprovedLeave: null, tenureMonths: 0, ...over,
});
const day = (d: EssDashboard, shiftName: string | null = null) => deriveDay({ t, fx, dashboard: d, shiftName, today: '2026-11-15', nowMs: NOW, leaveName: (_, s) => s });

test('the hero never claims what the payload did not say', () => {
  // No record: said plainly, no hours, no arrival.
  const none = day(dash());
  expect(none.pill.text).toBe('No attendance record for today');
  expect(none.figure).toEqual({ kind: 'text', text: 'Not in yet' });
  expect(none.facts).toEqual([]);

  // A provisional ("raw") record has no computed lateness, so "On time" is not claimed; a processed one may.
  const open = att({ firstInUtc: '2026-11-15T07:58:00Z' });
  expect(day(dash({ attendanceToday: open, attendanceTodaySource: 'raw' })).facts.map((f) => f.label)).not.toContain('Arrival');
  expect(day(dash({ attendanceToday: open, attendanceTodaySource: 'processed' })).facts[0]).toEqual({ value: 'On time', label: 'Arrival' });
  expect(day(dash({ attendanceToday: { ...open, lateMinutes: 12 }, attendanceTodaySource: 'processed' })).facts[0].value).toBe('Late by 12 min');
  expect(day(dash({ attendanceToday: open, attendanceTodaySource: 'raw' })).footnote).toContain('Provisional');

  // A missing punch is a problem state, not "Present".
  const missing = day(dash({ attendanceToday: att({ firstInUtc: '2026-11-15T07:58:00Z', missingPunch: true }) }));
  expect(missing.pill.tone).toBe('bad');
  expect(missing.figure.kind).toBe('text');

  // Overtime and the shift come from their own fields, and only when present.
  const facts = day(dash({ overtimeHoursThisMonth: 6 }), 'Day shift').facts;
  expect(facts).toEqual([{ value: 'Day shift', label: "Today's shift" }, { value: '{hours} h'.replace('{hours}', '6'), label: 'Approved overtime this month' }]);
});

test('leave covering today makes it a quiet day', () => {
  const d = dash({ nextApprovedLeave: { leaveTypeName: 'Annual Leave', startDate: '2026-11-15', endDate: '2026-11-19', days: 5 } });
  const v = day(d);
  expect(v.calm).toBe(true);
  expect(v.onLeave).toBe(true);
  expect(v.pill.text).toBe('On leave today');
  expect(day(dash({ nextApprovedLeave: { leaveTypeName: 'Annual Leave', startDate: '2026-11-20', endDate: '2026-11-22', days: 3 } })).onLeave).toBe(false);
});

const hr = (over: Partial<EssHrRequest> = {}): EssHrRequest => ({
  id: 'r-1', subject: 'Salary certificate', description: '', priority: 'Normal', status: 'Open', categoryName: 'Letters',
  dueAtUtc: '2026-11-17T00:00:00Z', createdAtUtc: '2026-11-12T09:14:00Z', hrResponded: false, isOverdue: false, responseStatus: 'Awaiting HR response', ...over,
});
const ctx = { t, fx, nowMs: NOW };

test('an HR request names its steps and what is current', () => {
  const waiting = hrRequestItem(hr(), new Set(), ctx);
  expect(waiting.steps.map((s) => [s.label, s.state])).toEqual([['Sent', 'done'], ['With HR', 'current'], ['HR replied', 'todo'], ['Closed', 'todo']]);
  expect(waiting.needsYou).toBe(false);

  const replied = hrRequestItem(hr({ responseStatus: 'Responded', hrResponded: true }), new Set(), ctx);
  expect(replied.needsYou).toBe(true);
  expect(replied.steps.map((s) => s.state)).toEqual(['done', 'done', 'current', 'todo']);
  expect(replied.action?.href).toBe('/ess/requests?open=r-1');
  // A reply already opened is not waiting for the employee any more.
  expect(hrRequestItem(hr({ responseStatus: 'Responded', hrResponded: true }), new Set(['r-1']), ctx).needsYou).toBe(false);

  const late = hrRequestItem(hr({ isOverdue: true, responseStatus: 'Overdue: HR has not replied yet' }), new Set(), ctx);
  expect(late.steps[1].state).toBe('problem');
});

const leave = (over: Partial<LeaveRequest> = {}): LeaveRequest => ({
  id: 'l-1', employeeId: 7, employeeName: '', departmentName: '', designationTitle: '', leaveTypeId: 'lt', leaveTypeName: 'Annual Leave', policyId: null,
  startDate: '2026-11-29', endDate: '2026-11-30', totalDays: 2, dayType: 'Full', hoursRequested: 0, reason: '', isEmergency: false, attachmentPath: '', payrollImpact: '',
  status: 'PendingHRApproval', managerApprovalNotes: '', hrApprovalNotes: '', rejectionReason: '', cancellationReason: '', returnDate: null, delegateEmployeeId: null,
  delegateEmployeeName: '', createdAtUtc: '2026-11-11T09:00:00Z', submittedAtUtc: '2026-11-11T09:00:00Z', decidedAtUtc: null, cancelledAtUtc: null, ...over,
});

test('leave shows only what is waiting or was just decided, with the stage it is at', () => {
  const name = (_: string | null, s: string) => s;
  const atHr = leaveItem(leave(), name, ctx)!;
  expect(atHr.steps.map((s) => [s.label, s.state])).toEqual([['Sent', 'done'], ['Manager approval', 'done'], ['HR approval', 'current'], ['Approved', 'todo']]);
  const atManager = leaveItem(leave({ status: 'PendingManagerApproval' }), name, ctx)!;
  expect(atManager.steps[1].state).toBe('current');
  // The approval rows that exist are used, and the stage still to come is named from the status.
  const rows = leaveItem(leave({ approvals: [{ id: 'a', leaveRequestId: 'l-1', stepNumber: 1, approverRole: 'Manager', approverId: null, approverName: 'Faisal', decision: 'Approved', notes: '', actedAtUtc: '2026-11-13T10:00:00Z', createdAtUtc: '' }] }), name, ctx)!;
  expect(rows.steps.map((s) => s.label)).toEqual(['Sent', 'Manager approval', 'HR approval', 'Approved']);
  expect(rows.steps[1].detail).toContain('Faisal');
  expect(rows.steps[2].state).toBe('current');
  // Old decisions and anything not pending are not in the tracker.
  expect(leaveItem(leave({ status: 'Approved', decidedAtUtc: ago(10 * 86_400_000) }), name, ctx)).toBeNull();
  expect(leaveItem(leave({ status: 'Cancelled' }), name, ctx)).toBeNull();
  const fresh = leaveItem(leave({ status: 'Approved', decidedAtUtc: ago(3600_000) }), name, ctx)!;
  expect(fresh.needsYou).toBe(true);
  expect(fresh.tone).toBe('emerald');
  const rejected = leaveItem(leave({ status: 'Rejected', decidedAtUtc: ago(3600_000), rejectionReason: 'Peak season' }), name, ctx)!;
  expect(rejected.steps.at(-1)).toMatchObject({ label: 'Not approved', state: 'problem', detail: 'Peak season' });
});

test('an advance is in the tracker until it is paid out', () => {
  const adv = (status: string, extra: Record<string, unknown> = {}) => ({ id: 'ad', employeeId: '7', advanceNumber: 'ADV-1', status, createdAtUtc: '2026-11-13T09:00:00Z', ...extra }) as never;
  expect(advanceItem(adv('Pending'), ctx, '/ess/loans')!.steps.map((s) => s.state)).toEqual(['done', 'current', 'todo', 'todo']);
  expect(advanceItem(adv('Approved'), ctx, null)!.steps.map((s) => s.state)).toEqual(['done', 'done', 'current', 'todo']);
  expect(advanceItem(adv('Approved', { paidAtUtc: '2026-11-14T00:00:00Z' }), ctx, null)).toBeNull();
  expect(advanceItem(adv('Cancelled'), ctx, null)).toBeNull();
});

test('a loan is in the tracker until it is paid out, as an advance is', () => {
  const loan = (status: string, extra: Record<string, unknown> = {}) => ({ id: 'ln', employeeId: '7', loanNumber: 'LN-1', loanTypeName: 'Personal', status, createdAtUtc: '2026-11-13T09:00:00Z', ...extra }) as never;
  const pending = loanItem(loan('Pending'), ctx, '/ess/loans')!;
  expect(pending.kind).toBe('loan');
  expect(pending.steps.map((s) => s.state)).toEqual(['done', 'current', 'todo', 'todo']);
  expect(pending.meta).toContain('LN-1');
  expect(pending.action?.href).toBe('/ess/loans');
  expect(loanItem(loan('Approved'), ctx, null)!.steps.map((s) => s.state)).toEqual(['done', 'done', 'current', 'todo']);
  expect(loanItem(loan('Approved', { disbursementDate: '2026-11-14' }), ctx, null)).toBeNull();
  expect(loanItem(loan('Active'), ctx, null)).toBeNull();
  expect(loanItem(loan('Rejected'), ctx, null)!.tone).toBe('rose');
  expect(loanItem(loan('Rejected', { createdAtUtc: '2026-10-01T00:00:00Z' }), ctx, null)).toBeNull();
  expect(loanItem(loan('Cancelled'), ctx, null)).toBeNull();
});

test('the tracker puts what waits for the employee first, then the newest', () => {
  const a = hrRequestItem(hr({ id: 'a', createdAtUtc: '2026-11-10T00:00:00Z' }), new Set(), ctx);
  const b = hrRequestItem(hr({ id: 'b', createdAtUtc: '2026-11-12T00:00:00Z' }), new Set(), ctx);
  const c = hrRequestItem(hr({ id: 'c', createdAtUtc: '2026-11-01T00:00:00Z', responseStatus: 'Responded', hrResponded: true }), new Set(), ctx);
  expect(orderTracker([a, b, c]).map((i) => i.id)).toEqual(['hr-c', 'hr-b', 'hr-a']);
});

const step = (over: Partial<Parameters<typeof buildNextStep>[0]> = {}) => buildNextStep({
  t, fx, dashboard: dash(), canWrite: true, today: '2026-11-15', unseenReplies: [], decidedLeave: [], openRequests: 0, sourceFailed: false, onLeave: false, ...over,
}).step;

test('"your next step" is the first thing that needs the employee, never a false all-clear', () => {
  // Missing punch outranks an unread reply.
  const withBoth = step({ dashboard: dash({ attendanceToday: att({ missingPunch: true, firstInUtc: '2026-11-15T07:58:00Z' }) }), unseenReplies: [hr({ responseStatus: 'Responded' })] });
  expect(withBoth.title).toBe('A punch is missing from today');
  expect(withBoth.cta?.href).toMatch(/^\/ess\/requests\?subject=/);
  // Read-only: the problem is still named, with nothing to press.
  expect(step({ canWrite: false, dashboard: dash({ attendanceToday: att({ missingPunch: true }) }) }).cta).toBeNull();

  const reply = step({ unseenReplies: [hr({ responseStatus: 'Responded' })] });
  expect(reply.cta).toEqual({ label: 'Read the reply', href: '/ess/requests?open=r-1' });
  expect(reply.titleMessage?.values).toEqual({ subject: 'Salary certificate' });

  const doc = step({ dashboard: dash({ documentAlerts: [{ id: 'd1', documentType: 'Passport', fileName: 'p.pdf', expiryDate: '2026-12-20', approvalStatus: 'Approved' }] }) });
  expect(doc.title).toBe('Passport is expiring');

  // Nothing waiting: calm, and honest about open requests.
  expect(step().calm).toBe(true);
  expect(step().why).toBe('You have no open requests.');
  expect(step({ openRequests: 2 }).why).toContain('still being handled');
  // A list that failed to load is not "all clear".
  const failed = step({ sourceFailed: true });
  expect(failed.calm).toBe(false);
  expect(failed.cta).toEqual({ label: 'Retry', retry: true });
  expect(step({ onLeave: true }).title).toBe('Enjoy your time off');
  expect(step({ canWrite: false }).cta?.href).toBe('/ess/payslips');
});

test('with more than fit, what needs the employee stays and the furthest plain dates go', () => {
  const e = (id: string, date: string | null, tone: UpcomingEntry['tone'] = 'normal'): UpcomingEntry => ({ id, date, tone, label: id });
  const all = [e('leave', '2026-12-15'), e('pay', '2026-11-26'), e('loan', '2026-12-01'), e('passport', '2026-12-20', 'warn'), e('task', '2026-11-20', 'warn'), e('pending', '2026-11-29', 'pending'), e('undated', null, 'warn')];
  const { shown, hidden } = pickUpcoming(all, 5);
  expect(shown.map((x) => x.id)).toEqual(['task', 'pay', 'pending', 'passport', 'undated']);
  expect(hidden).toBe(2);
  expect(pickUpcoming(all.slice(0, 3), 5)).toEqual({ shown: [all[1], all[2], all[0]], hidden: 0 });
});
