import test from 'node:test';
import assert from 'node:assert/strict';
import { changeRequestStatus, mapChangeRequests } from '../src/features/profile/changeRequestModel.ts';

// GET /api/ess/profile-change-requests/mine: the employee's own older (mobile) requests and where each stands.
test('requests map newest first with their fields and HR note', () => {
  const rows = mapChangeRequests([
    { id: 'a', status: 'Rejected', createdAtUtc: '2026-10-20T08:00:00Z', fields: ['personalEmail'], reviewNote: 'That address bounces.' },
    { id: 'b', status: 'PendingHR', createdAtUtc: '2026-11-02T08:00:00Z', fields: ['phone', 'emergencyContactName'], reviewNote: null },
  ]);
  assert.deepEqual(rows.map((r) => r.id), ['b', 'a']);
  assert.deepEqual(rows[0].fields, ['phone', 'emergencyContactName']);
  assert.equal(rows[0].note, null);
  assert.equal(rows[1].note, 'That address bounces.');
});

test('unreadable rows are dropped, not invented', () => {
  assert.deepEqual(mapChangeRequests(null), []);
  assert.deepEqual(mapChangeRequests([{ status: 'PendingHR' }, 5, null]), []);
});

test('every server status has a state; an unknown one is kept as other', () => {
  assert.equal(changeRequestStatus('PendingHR'), 'pendingHr');
  assert.equal(changeRequestStatus('AwaitingFinance'), 'awaitingFinance');
  assert.equal(changeRequestStatus('Approved'), 'approved');
  assert.equal(changeRequestStatus('Rejected'), 'rejected');
  assert.equal(changeRequestStatus('Expired'), 'expired');
  assert.equal(changeRequestStatus('Weird'), 'other');
});
