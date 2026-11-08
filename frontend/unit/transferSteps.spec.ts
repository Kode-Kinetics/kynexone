import { test, expect } from '@playwright/test';
import { transferApprovePath, transferClosePath, transferHolderLine, transferOutcomeLine, transferTitle, type EmployeeTransferView } from '../src/lib/transferSteps';

// G-002: a transfer waits on a named step; every surface says which one, who holds it, and which endpoint approves it.
const t = (key: string, vars?: Record<string, string | number>) => key.replace(/\{(\w+)\}/g, (_, name: string) => String(vars?.[name] ?? ''));

const view = (over: Partial<EmployeeTransferView> = {}): EmployeeTransferView => ({
  transfer: { id: 'tr-1', employeeId: 7, currentBranch: 'Riyadh', currentDepartment: 'HR', currentDesignation: 'Officer', newBranch: '', newDepartment: 'Finance', newDesignation: '', effectiveDate: '2026-12-01', reason: 'Restructure', status: 'PendingCurrentManager', createdAtUtc: '2026-11-01T00:00:00Z' },
  employeeName: 'Sam Subject', employeeCode: 'E1', currentManagerName: 'Cora Current', newManagerName: 'Nadia New',
  awaitingStep: 'CurrentManager', awaitingName: 'Cora Current', canDecide: true, canReject: true, canWithdraw: false, ...over,
});

test('each step approves through its own endpoint', () => {
  expect(transferApprovePath('tr-1', 'CurrentManager')).toBe('/api/employees/transfers/tr-1/approve-current-manager');
  expect(transferApprovePath('tr-1', 'NewManager')).toBe('/api/employees/transfers/tr-1/approve-new-manager');
  expect(transferApprovePath('tr-1', 'Hr')).toBe('/api/employees/transfers/tr-1/approve-hr');
});

test('the holder line names the step and the person who holds it', () => {
  expect(transferHolderLine(view(), t)).toBe('Waiting for the current manager: Cora Current');
  expect(transferHolderLine(view({ awaitingStep: 'NewManager', awaitingName: 'Nadia New' }), t)).toBe('Waiting for the new manager: Nadia New');
  expect(transferHolderLine(view({ awaitingStep: 'Hr', awaitingName: null }), t)).toBe('Waiting for HR approval');
});

test('a step nobody can be named for still says which step it is', () => {
  expect(transferHolderLine(view({ awaitingName: null }), t)).toBe('Waiting for the current manager (HR can decide it)');
});

test('an applied transfer says so instead of naming a holder', () => {
  const done = view({ awaitingStep: null, awaitingName: null, canDecide: false });
  done.transfer.status = 'ApprovedApplied';
  expect(transferHolderLine(done, t)).toBe('Applied');
});

test('the title says who moves where', () => {
  expect(transferTitle(view(), t)).toBe('Sam Subject: Finance');
  const branchOnly = view(); branchOnly.transfer.newDepartment = ''; branchOnly.transfer.newBranch = 'Jeddah';
  expect(transferTitle(branchOnly, t)).toBe('Sam Subject: Jeddah');
  const bare = view(); bare.transfer.newDepartment = ''; bare.transfer.newDesignation = '';
  expect(transferTitle(bare, t)).toBe('Sam Subject: Transfer request');
});

test('reject and withdraw have their own endpoints', () => {
  expect(transferClosePath('tr-1', 'reject')).toBe('/api/employees/transfers/tr-1/reject');
  expect(transferClosePath('tr-1', 'withdraw')).toBe('/api/employees/transfers/tr-1/withdraw');
});

test('a rejected or withdrawn transfer says so, with who and why when the server sent them', () => {
  const rejected = view({ awaitingStep: null, awaitingName: null, canDecide: false, canReject: false, decisionReason: 'Budget frozen', decidedByName: 'Hana HR' });
  rejected.transfer.status = 'Rejected';
  expect(transferHolderLine(rejected, t)).toBe('Rejected');
  const withdrawn = view({ awaitingStep: null, awaitingName: null, canDecide: false, canReject: false });
  withdrawn.transfer.status = 'Withdrawn';
  expect(transferHolderLine(withdrawn, t)).toBe('Withdrawn');
  expect(transferOutcomeLine(rejected)).toBe('Budget frozen (Hana HR)');
  expect(transferOutcomeLine(withdrawn)).toBeNull();
  const noName = view({ awaitingStep: null, decisionReason: 'Budget frozen', decidedByName: null });
  expect(transferOutcomeLine(noName)).toBe('Budget frozen');
});
