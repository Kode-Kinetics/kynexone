import test from 'node:test';
import assert from 'node:assert/strict';
import {
  leaveCancellationInbox,
  leaveCancellationOutcome,
  mapLeaveCancellation,
  mergeInbox,
  summarizeInbox,
} from '../src/features/approvals/approvalModel.ts';
import { cancellationHoldersFrom } from '../src/features/leave/leaveLogic.ts';

// Shape of one row of GET /api/leave/requests/awaiting-decision (kind: "Cancellation").
const row = {
  id: 'leave-1',
  employeeId: 9,
  employeeName: 'Rania Saleh',
  leaveTypeName: 'Annual Leave',
  startDate: '2026-12-10',
  endDate: '2026-12-12',
  totalDays: 3,
  status: 'CancellationRequested',
  reason: 'Trip',
  submittedAtUtc: '2026-11-01T08:00:00Z',
  kind: 'Cancellation',
  cancellationReason: 'Plans changed',
  cancellationRequestedAtUtc: '2026-11-07T08:00:00Z',
};

test('a pending cancellation becomes a decidable leave inbox item that says what was asked', () => {
  const item = mapLeaveCancellation(row);
  assert.equal(item.source, 'leaveCancellation');
  assert.equal(item.kind, 'LEAVE');
  assert.equal(item.id, 'leave-1');
  assert.equal(item.entityId, 'leave-1');
  assert.equal(item.key, 'leaveCancellation:leave-1');
  assert.equal(item.requesterName, 'Rania Saleh');
  assert.equal(item.requesterEmployeeId, 9);
  assert.equal(item.subject, 'Annual Leave');
  assert.equal(item.canDecide, true);
  assert.deepEqual(item.cancellation, { reason: 'Plans changed', requestedAt: '2026-11-07T08:00:00Z' });
  assert.equal(item.submittedAt, '2026-11-07T08:00:00Z', 'it has been waiting since the employee asked, not since the leave was filed');
});

test('no reason given stays null; nothing is made up', () => {
  const item = mapLeaveCancellation({ ...row, cancellationReason: '  ', cancellationRequestedAtUtc: null });
  assert.equal(item.cancellation?.reason, null);
  assert.equal(item.submittedAt, null);
});

test('only Cancellation rows enter the inbox, and a capped server read says the list may be incomplete', () => {
  const complete = leaveCancellationInbox({ items: [row, { ...row, id: 'leave-2', kind: 'Decision' }], total: 2, truncated: false });
  assert.deepEqual(complete.items.map((i) => i.id), ['leave-1']);
  assert.equal(complete.incomplete, false);
  const capped = leaveCancellationInbox({ items: [row], total: 1500, truncated: true });
  assert.equal(capped.incomplete, true);
  assert.deepEqual(leaveCancellationInbox(null), { items: [], incomplete: false });
  assert.deepEqual(leaveCancellationInbox({ items: 'nope' }), { items: [], incomplete: false });
});

test('approve cancellation is a final approval; keep leave is a refusal', () => {
  assert.deepEqual(leaveCancellationOutcome('approve'), { kind: 'approved' });
  assert.deepEqual(leaveCancellationOutcome('reject'), { kind: 'rejected' });
});

test('cancellations merge with the other sources and count as leave', () => {
  const merged = mergeInbox([mapLeaveCancellation(row)], [mapLeaveCancellation({ ...row, id: 'leave-2' })]);
  assert.equal(merged.length, 2);
  assert.equal(summarizeInbox(merged).byKind.LEAVE, 2);
});

test('the employee is told who holds their cancellation, from the leave detail', () => {
  assert.deepEqual(cancellationHoldersFrom({ cancellation: { status: 'Pending', awaiting: ['Mona Manager', ' Hana HR ', ''] } }), ['Mona Manager', 'Hana HR']);
  assert.deepEqual(cancellationHoldersFrom({ cancellation: { status: 'Pending', awaiting: [] } }), []);
  assert.deepEqual(cancellationHoldersFrom({ cancellation: null }), []);
  assert.deepEqual(cancellationHoldersFrom(null), []);
});
