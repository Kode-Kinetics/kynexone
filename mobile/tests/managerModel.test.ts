import test from 'node:test';
import assert from 'node:assert/strict';
import {
  balanceFacts,
  givenName,
  mapLeaveFacts,
  mapOvertimeApproval,
  mapWorkflowApproval,
  mergeInbox,
  myDecisionsFrom,
  overtimeOutcome,
  queueTarget,
  roleKey,
  summarizeInbox,
  workflowOutcome,
} from '../src/features/approvals/approvalModel.ts';
import {
  addDays,
  flattenTeamCalendar,
  overlapping,
  peopleOut,
  TEAM_BUCKETS,
  teamBucket,
  teamTodayCounts,
  whosOutByDay,
} from '../src/features/approvals/teamModel.ts';

// Shapes copied from the local demo API (tenant masar, manager persona), 2026-10-10.
const workflowRow = {
  id: 'a08bc1b3-c351-43b7-8f5c-5d5bc386f798',
  entityName: 'LeaveRequest',
  entityId: 'a08bc1b3-c351-43b7-8f5c-5d5bc386f798',
  title: 'Annual Leave — Rania Saleh',
  status: 'Pending',
  currentStepOrder: 1,
  requestedForEmployeeId: 7,
  currentQueue: 'Manager:Majed Al-Ghamdi',
  dueAtUtc: '2026-10-12T17:46:25.0024Z',
  isOverdue: false,
  priority: 'Normal',
  createdAtUtc: '2026-10-10T17:46:24.907696Z',
  decisions: [],
  canDecide: true,
  decisionBlockedReason: null,
  changeSummary: null,
};

const overtimeRow = {
  id: '4b02f652-c13b-47a8-9a28-83e6816d615a',
  employeeId: 7,
  employeeName: 'Rania Saleh',
  workDate: '2026-10-08',
  startTimeUtc: '2026-10-08T13:30:00Z',
  endTimeUtc: '2026-10-08T16:00:00Z',
  requestedMinutes: 150,
  reason: 'Payroll variance review for October run',
  status: 'PendingManager',
  createdAtUtc: '2026-10-10T17:43:33.607603Z',
};

test('workflow approval maps real fields and fabricates nothing', () => {
  const item = mapWorkflowApproval(workflowRow);
  assert.equal(item.key, `workflow:${workflowRow.id}`);
  assert.equal(item.kind, 'LEAVE');
  assert.equal(item.subject, 'Annual Leave');
  assert.equal(item.requesterName, 'Rania Saleh');
  assert.equal(item.requesterEmployeeId, 7);
  assert.equal(item.submittedAt, workflowRow.createdAtUtc);
  assert.equal(item.dueAt, workflowRow.dueAtUtc);
  assert.equal(item.stepOrder, 1);
  assert.equal(item.canDecide, true);
  // No urgency field exists at all; no date or name is invented when the server omits them.
  assert.ok(!('urgency' in item));
  const bare = mapWorkflowApproval({ id: 'x', status: 'Pending' });
  assert.equal(bare.submittedAt, null);
  assert.equal(bare.dueAt, null);
  assert.equal(bare.requesterName, null);
  assert.equal(bare.isOverdue, false);
  // canDecide must be granted by the server, never assumed.
  assert.equal(bare.canDecide, false);
});

test('overtime joins the inbox only at the manager stage and never for the manager themself', () => {
  const item = mapOvertimeApproval(overtimeRow, 2);
  assert.ok(item);
  assert.equal(item.source, 'overtime');
  assert.equal(item.kind, 'OVERTIME');
  assert.equal(item.requesterName, 'Rania Saleh');
  assert.equal(item.overtime?.minutes, 150);
  assert.equal(item.dueAt, null);
  assert.equal(item.isOverdue, false);
  assert.equal(mapOvertimeApproval({ ...overtimeRow, status: 'PendingHR' }, 2), null);
  assert.equal(mapOvertimeApproval({ ...overtimeRow, status: 'Approved' }, 2), null);
  assert.equal(mapOvertimeApproval(overtimeRow, 7), null, 'own overtime is not the manager’s to decide');
});

test('merged inbox is exception-first and each item appears once', () => {
  const overdue = mapWorkflowApproval({ ...workflowRow, id: 'od', isOverdue: true, dueAtUtc: '2026-10-09T00:00:00Z' });
  const soon = mapWorkflowApproval({ ...workflowRow, id: 'soon', dueAtUtc: '2026-10-11T00:00:00Z' });
  const later = mapWorkflowApproval(workflowRow);
  const ot = mapOvertimeApproval(overtimeRow, 2)!;
  const merged = mergeInbox([later, soon, later], [ot, overdue]);
  assert.deepEqual(merged.map((i) => i.id), ['od', 'soon', workflowRow.id, overtimeRow.id]);
  const summary = summarizeInbox(merged);
  assert.equal(summary.total, 4);
  assert.equal(summary.overdue, 1);
  assert.deepEqual(summary.byKind, { LEAVE: 3, OVERTIME: 1 });
  assert.equal(summary.lead?.id, 'od');
  // With nothing overdue the lead is the request waiting longest.
  const calm = summarizeInbox(mergeInbox([later], [ot]));
  assert.equal(calm.lead?.id, overtimeRow.id);
  assert.equal(summarizeInbox([]).lead, null);
});

test('a multi-step approval reports its true state, not "Approved"', () => {
  assert.deepEqual(
    workflowOutcome({ status: 'Pending', currentStepOrder: 2, currentQueue: 'Role:HR Manager' }),
    { kind: 'movedOn', next: { type: 'role', name: 'HR Manager' } },
  );
  assert.deepEqual(workflowOutcome({ status: 'Approved' }), { kind: 'approved' });
  assert.deepEqual(workflowOutcome({ status: 'Rejected' }), { kind: 'rejected' });
  assert.deepEqual(workflowOutcome({ status: 'ReturnedToRequester' }), { kind: 'returned' });
  assert.deepEqual(workflowOutcome({}), { kind: 'recorded' }, 'no status → no claimed outcome');
  // Overtime: a manager approval moves it to HR; the final calc has an amount but no status.
  assert.deepEqual(overtimeOutcome({ status: 'PendingHR' }), { kind: 'movedOn', next: { type: 'role', name: 'HR Manager' } });
  assert.deepEqual(overtimeOutcome({ amount: 312.5, approvedMinutes: 150 }), { kind: 'approved' });
  assert.deepEqual(overtimeOutcome({ status: 'Rejected' }), { kind: 'rejected' });
  assert.deepEqual(overtimeOutcome(null), { kind: 'recorded' });
});

test('queue targets name a role or a person; known roles can be translated', () => {
  assert.deepEqual(queueTarget('Role:HR Manager'), { type: 'role', name: 'HR Manager' });
  assert.deepEqual(queueTarget('Manager:Majed Al-Ghamdi'), { type: 'person', name: 'Majed Al-Ghamdi' });
  assert.equal(queueTarget(''), null);
  assert.equal(queueTarget('Role:'), null);
  assert.equal(roleKey('HR Manager'), 'hrManager');
  assert.equal(roleKey('Chief Wizard'), null);
});

test('history keeps only requests this user decided, newest first, with where each stands now', () => {
  const me = '7ec1a367-3d18-4c1f-8a35-c77199ae536f';
  const rows = [
    {
      id: 'r1', entityName: 'LeaveRequest', title: 'Annual Leave — Shaimaa Mostafa', status: 'Approved', currentQueue: '',
      decisions: [
        { stepOrder: 1, decision: 'Approved', comments: 'Hand over the PO queue.', decidedByUserId: me, decidedAtUtc: '2026-10-10T17:46:41Z' },
        { stepOrder: 2, decision: 'Approved', comments: 'Approved by HR.', decidedByUserId: 'hr', decidedAtUtc: '2026-10-10T17:46:42Z' },
      ],
    },
    { id: 'r2', entityName: 'LeaveRequest', title: 'Annual Leave — Mohammed', status: 'Approved', decisions: [{ decision: 'Approved', decidedByUserId: 'hr', decidedAtUtc: '2026-10-10T17:45:34Z' }] },
    { id: 'r3', entityName: 'LeaveRequest', title: 'Sick Leave — Rania Saleh', status: 'Pending', currentQueue: 'Role:HR Manager', decisions: [{ decision: 'Approved', decidedByUserId: me.toUpperCase(), decidedAtUtc: '2026-10-10T18:00:00Z' }] },
  ];
  const mine = myDecisionsFrom(rows, me);
  assert.deepEqual(mine.map((d) => d.key), ['r3', 'r1']);
  assert.equal(mine[0].state, 'pending');
  assert.deepEqual(mine[0].nowWith, { type: 'role', name: 'HR Manager' });
  assert.equal(mine[1].state, 'approved');
  assert.equal(mine[1].myDecision, 'approved');
  assert.equal(mine[1].comments, 'Hand over the PO queue.');
  assert.equal(mine[1].nowWith, null);
  assert.deepEqual(myDecisionsFrom(rows, ''), []);
});

test('leave balance: "left if approved" only when the pending reservation covers this request', () => {
  const leave = mapLeaveFacts({
    request: { id: 'l1', employeeId: 7, leaveTypeId: 'annual', leaveTypeName: 'Annual Leave', startDate: '2026-10-28', endDate: '2026-10-28', totalDays: 1, status: 'PendingManagerApproval' },
  })!;
  const balances = [
    { leaveTypeId: 'annual', leaveTypeName: 'Annual Leave', year: 2026, available: 14, pending: 5 },
    { leaveTypeId: 'sick', year: 2026, available: 30, pending: 0 },
  ];
  assert.deepEqual(balanceFacts(balances, leave), { leaveTypeName: 'Annual Leave', available: 14, afterApproval: 14 });
  // Pending smaller than this request: we cannot show it is reserved, so no "after" figure.
  assert.equal(balanceFacts([{ ...balances[0], pending: 0 }], leave)?.afterApproval, null);
  // Another year's balance is not this request's balance.
  assert.equal(balanceFacts([{ ...balances[0], year: 2025 }], leave), null);
  assert.equal(balanceFacts([], leave), null);
});

test('team buckets: one definition, every member in exactly one bucket, buckets add up to the total', () => {
  const statuses = ['Present', 'Late', 'Half day', 'Not clocked in', 'Absent', 'On leave', 'Missing punch', 'Holiday', 'Weekend', 'Early leave', ''];
  const counts = teamTodayCounts(statuses.map((rawStatus) => ({ rawStatus })));
  assert.equal(counts.total, statuses.length);
  assert.equal(TEAM_BUCKETS.reduce((sum, b) => sum + counts[b], 0), counts.total);
  // Present includes late arrivals and half days — the same number on Home and on Team.
  assert.equal(counts.present, 3);
  assert.equal(counts.late, 1);
  // No punch yet is "not clocked in", not "absent".
  assert.equal(teamBucket('Not clocked in'), 'notIn');
  assert.equal(counts.notIn, 2);
  assert.equal(counts.absent, 1);
  assert.equal(counts.onLeave, 1);
  assert.equal(counts.missingPunch, 1);
  assert.equal(counts.off, 2);
  assert.equal(counts.other, 1);
});

test("who's out: flattens the department groups and lists each day's absences", () => {
  const groups = [{
    departmentName: 'Finance & Accounts',
    employees: [
      { employeeId: 29, employeeName: 'Shaimaa Mostafa', leaveTypeName: 'Annual Leave', startDate: '2026-10-22', endDate: '2026-10-22', days: 1, status: 'Approved' },
      { employeeId: 3, employeeName: 'Mohammed Abdelrahman', leaveTypeName: 'Annual Leave', startDate: '2026-10-19', endDate: '2026-10-20', days: 2, status: 'Approved' },
      { employeeId: 9, employeeName: 'Legacy', startDate: '2026-10-20', endDate: '2026-10-21', status: 'Submitted' },
      { employeeId: 10, employeeName: 'Broken', startDate: null, endDate: '2026-10-21' },
    ],
  }];
  const entries = flattenTeamCalendar(groups);
  assert.deepEqual(entries.map((e) => e.employeeName), ['Mohammed Abdelrahman', 'Legacy', 'Shaimaa Mostafa']);
  assert.equal(entries[1].approved, false, 'a not-yet-approved row is flagged, not shown as approved');
  assert.equal(flattenTeamCalendar(null).length, 0);

  const days = whosOutByDay(entries, '2026-10-18', 14);
  assert.equal(days.length, 14);
  assert.equal(days[0].date, '2026-10-18');
  assert.equal(days[13].date, '2026-10-31');
  assert.deepEqual(days[2].out.map((e) => e.employeeId), [3, 9]); // 20 Oct
  assert.deepEqual(days[4].out.map((e) => e.employeeId), [29]); // 22 Oct
  assert.equal(days[0].out.length, 0);
  assert.equal(peopleOut(entries), 3);

  // Leave overlap for a decision excludes the requester.
  assert.deepEqual(overlapping(entries, '2026-10-20', '2026-10-22', 9).map((e) => e.employeeId), [3, 29]);
  assert.equal(addDays('2026-12-31', 1), '2027-01-01');
  assert.equal(addDays('2026-03-01', -1), '2026-02-28');
});

test('given name keeps compound Arabic first names whole', () => {
  assert.equal(givenName('Majed Al-Ghamdi'), 'Majed');
  assert.equal(givenName('Abdul Rahman Al-Qahtani'), 'Abdul Rahman');
  assert.equal(givenName('عبد الله الشهري'), 'عبد الله');
  assert.equal(givenName('  '), null);
  assert.equal(givenName(undefined), null);
});

test('day state: absence only counts on a working day; a missing punch is never "absent"', async () => {
  const { teamBucket: bucket, teamTodayCounts: counts, BUCKET_TONE, holidayOn } = await import('../src/features/approvals/teamModel.ts');
  // Saturday 10 Oct 2026 in the demo: /ess/team says "Not clocked in" for all three reports.
  const weekendTeam = [{ rawStatus: 'Not clocked in' }, { rawStatus: 'Not clocked in' }, { rawStatus: 'Not clocked in' }];
  const c = counts(weekendTeam);
  assert.equal(c.absent, 0, 'no punch is not an absence');
  assert.equal(c.notIn, 3);
  assert.equal(BUCKET_TONE.notIn, 'neutral', 'and it is not painted red');
  // On a public holiday, no-punch and even a processed Absent count as off.
  assert.equal(bucket('Not clocked in', { publicHoliday: true }), 'off');
  assert.equal(bucket('Absent', { publicHoliday: true }), 'off');
  assert.equal(bucket('Absent', { restDay: true }), 'off');
  assert.equal(bucket('Present', { publicHoliday: true }), 'present', 'someone who worked still counts');
  assert.equal(bucket('Absent'), 'absent');
  assert.equal(counts(weekendTeam, { publicHoliday: true }).off, 3);
  // Holiday lookup from GET /leave/holidays/range.
  const rows = [
    { date: '2026-09-23', nameEn: 'Saudi National Day', nameAr: 'اليوم الوطني', isOptional: false },
    { date: '2026-10-11', nameEn: 'Optional day', isOptional: true },
  ];
  assert.deepEqual(holidayOn(rows, '2026-09-23'), { nameEn: 'Saudi National Day', nameAr: 'اليوم الوطني' });
  assert.equal(holidayOn(rows, '2026-10-11'), null, 'optional holidays are not company-wide days off');
  assert.equal(holidayOn(null, '2026-09-23'), null);
});

test('calendar day difference for document expiry', async () => {
  const { daysBetween } = await import('../src/features/approvals/teamModel.ts');
  assert.equal(daysBetween('2026-10-10', '2026-10-10'), 0);
  assert.equal(daysBetween('2026-10-10', '2026-12-09'), 60);
  assert.equal(daysBetween('2026-10-10', '2026-10-09T00:00:00Z'), -1);
  assert.equal(daysBetween('2026-10-10', ''), null);
});

test('every enum the screens translate has an English and Arabic string', async () => {
  const { managerStrings } = await import('../src/config/strings/managerStrings.ts');
  const { KNOWN_ROLES } = await import('../src/features/approvals/approvalModel.ts');
  const kinds = ['LEAVE', 'OVERTIME', 'ATTENDANCE_CORRECTION', 'LOAN', 'ADVANCE', 'PAYROLL', 'RECRUITMENT', 'APPRAISAL', 'EMPLOYEE_CHANGE', 'OTHER'];
  for (const lang of ['en', 'ar'] as const) {
    const s = managerStrings[lang] as any;
    for (const k of kinds) assert.ok(s.kind[k], `${lang} kind.${k}`);
    for (const r of new Set(Object.values(KNOWN_ROLES))) assert.ok(s.role[r], `${lang} role.${r}`);
    for (const b of TEAM_BUCKETS) assert.ok(s.status[b], `${lang} status.${b}`);
    for (const v of ['approved', 'rejected', 'returned', 'other']) assert.ok(s.approvals.verb[v], `${lang} verb.${v}`);
  }
});
