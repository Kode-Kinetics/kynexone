import test from 'node:test';
import assert from 'node:assert/strict';
import { mapTransferApproval, transferDecision, transferOutcome, mergeInbox } from '../src/features/approvals/approvalModel.ts';

// GET /api/employees/transfers/pending (EmployeeTransferViewDto, camelCase). Transfers are not ApprovalRequests: the web
// Approval Center and this inbox list them from that endpoint.
const row = (over: Record<string, unknown> = {}, step = 'CurrentManager') => ({
  transfer: {
    id: 'tr-1', employeeId: 901, newDepartment: 'Finance', newBranch: '', newDesignation: '', effectiveDate: '2026-12-01',
    reason: 'Restructure', status: 'PendingCurrentManager', createdAtUtc: '2026-11-01T08:00:00Z',
  },
  employeeName: 'Sam Subject', employeeCode: 'E-0901', awaitingStep: step, awaitingName: 'Cora Current', canDecide: true, canReject: true, ...over,
});

test('a transfer the caller may decide becomes an inbox item with its step and target', () => {
  const item = mapTransferApproval(row());
  assert.ok(item);
  assert.equal(item.key, 'transfer:tr-1');
  assert.equal(item.source, 'transfer');
  assert.equal(item.kind, 'TRANSFER');
  assert.equal(item.requesterName, 'Sam Subject');
  assert.equal(item.canDecide, true);
  assert.deepEqual(item.transfer, { awaitingStep: 'CurrentManager', target: 'Finance', effectiveDate: '2026-12-01', reason: 'Restructure', canReject: true });
  assert.equal(item.submittedAt, '2026-11-01T08:00:00Z');
});

test('a transfer the caller may not decide is not offered', () => {
  assert.equal(mapTransferApproval(row({ canDecide: false })), null);
  assert.equal(mapTransferApproval(row({ awaitingStep: null })), null);
  assert.equal(mapTransferApproval(null), null);
});

test('the target falls back to branch, then designation, then nothing invented', () => {
  const r = row();
  r.transfer.newDepartment = ''; r.transfer.newBranch = 'Jeddah';
  assert.equal(mapTransferApproval(r)?.transfer?.target, 'Jeddah');
  r.transfer.newBranch = ''; r.transfer.newDesignation = 'Lead';
  assert.equal(mapTransferApproval(r)?.transfer?.target, 'Lead');
  r.transfer.newDesignation = '';
  assert.equal(mapTransferApproval(r)?.transfer?.target, null);
});

test('each step approves through its own endpoint and reject needs a reason', () => {
  const at = (step: string) => mapTransferApproval(row({}, step))!;
  assert.deepEqual(transferDecision(at('CurrentManager'), 'approve', undefined), { path: '/employees/transfers/tr-1/approve-current-manager', body: undefined });
  assert.deepEqual(transferDecision(at('NewManager'), 'approve', undefined), { path: '/employees/transfers/tr-1/approve-new-manager', body: undefined });
  assert.deepEqual(transferDecision(at('Hr'), 'approve', undefined), { path: '/employees/transfers/tr-1/approve-hr', body: undefined });
  assert.deepEqual(transferDecision(at('Hr'), 'reject', '  Budget frozen '), { path: '/employees/transfers/tr-1/reject', body: { reason: 'Budget frozen' } });
  assert.throws(() => transferDecision(at('Hr'), 'reject', '   '), /reason/i);
});

test('the outcome says the true state: only HR approval applies it', () => {
  assert.deepEqual(transferOutcome('Hr', 'approve'), { kind: 'approved' });
  assert.deepEqual(transferOutcome('CurrentManager', 'approve'), { kind: 'movedOn', next: null });
  assert.deepEqual(transferOutcome('NewManager', 'approve'), { kind: 'movedOn', next: { type: 'role', name: 'HR Manager' } });
  assert.deepEqual(transferOutcome('Hr', 'reject'), { kind: 'rejected' });
});

test('transfers join the inbox with the other sources', () => {
  const t = mapTransferApproval(row())!;
  assert.equal(mergeInbox([t]).length, 1);
});
