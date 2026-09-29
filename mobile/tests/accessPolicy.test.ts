import test from 'node:test';
import assert from 'node:assert/strict';
import {
  BACKEND_ACCESS_MODES,
  deriveMobileAccess,
  normalizeAccessMode,
} from '../src/auth/accessPolicy.ts';
import type { MobileSurface } from '../src/auth/accessPolicy.ts';
import type { AccessMode, AuthUser } from '../src/types/index.ts';

function user(mode: AccessMode, role: AuthUser['role'] = 'EMPLOYEE', permissions: string[] = []): AuthUser {
  const grouped = new Map<string, string[]>();
  for (const permission of permissions) {
    const [module, action] = permission.split('.');
    grouped.set(module, [...(grouped.get(module) ?? []), action]);
  }
  return {
    id: 'u1', tenantId: 't1', employeeId: '1', username: 'u@example.com', email: 'u@example.com',
    fullName: 'Test User', role, accessMode: mode,
    permissions: [...grouped].map(([module, actions]) => ({ module, actions })),
    isFirstLogin: false, isActive: true, mustChangePassword: false,
  };
}

test('recognizes every backend access mode and fails unknown modes closed', () => {
  assert.equal(BACKEND_ACCESS_MODES.length, 11);
  for (const mode of BACKEND_ACCESS_MODES) assert.equal(normalizeAccessMode(mode.toUpperCase()), mode);
  assert.equal(normalizeAccessMode('future-super-mode'), 'NoLogin');
  assert.equal(normalizeAccessMode(undefined), 'NoLogin');
});

test('all modes without effective permissions expose Account only', () => {
  for (const mode of BACKEND_ACCESS_MODES) {
    const policy = deriveMobileAccess(user(mode));
    assert.deepEqual([...policy.surfaces], ['account'], mode);
    assert.equal(policy.landing, 'Account', mode);
  }
});

test('all eleven access modes enforce their positive ceiling', () => {
  const permissions = ['ess.read', 'ess.write', 'manager.read', 'approvals.decide', 'attendance.read', 'attendance.kiosk'];
  const expected: Record<AccessMode, readonly MobileSurface[]> = {
    FullPortal: ['managerHome', 'team', 'approvals', 'attendance', 'leave'],
    ESSOnly: ['employeeHome', 'attendance', 'leave'],
    ManagerPortal: ['managerHome', 'team', 'approvals', 'attendance', 'leave'],
    HRPortal: ['managerHome', 'team', 'approvals', 'attendance', 'leave'],
    PayrollPortal: ['employeeHome', 'approvals', 'attendance', 'payslips'],
    FinancePortal: ['employeeHome', 'approvals', 'attendance', 'payslips'],
    SupervisorPortal: ['managerHome', 'team', 'approvals', 'attendance', 'leave'],
    ReadOnlyAuditor: ['team', 'approvals', 'attendance'],
    Mobile: ['employeeHome', 'attendance', 'leave'],
    KioskOnly: ['kioskPunch', 'attendance'],
    NoLogin: [],
  };
  for (const mode of BACKEND_ACCESS_MODES) {
    const policy = deriveMobileAccess(user(mode, 'EMPLOYEE', permissions));
    for (const surface of expected[mode]) assert.equal(policy.surfaces.has(surface), true, `${mode}:${surface}`);
    if (mode === 'NoLogin') assert.deepEqual([...policy.surfaces], ['account']);
  }
});

test('kiosk requires attendance.kiosk and exposes only punch, history, account', () => {
  const denied = deriveMobileAccess(user('KioskOnly', 'EMPLOYEE', ['attendance.read']));
  assert.deepEqual([...denied.surfaces], ['account']);
  const allowed = deriveMobileAccess(user('KioskOnly', 'EMPLOYEE', ['attendance.kiosk']));
  assert.deepEqual([...allowed.surfaces].sort(), ['account', 'attendance', 'kioskPunch']);
  assert.equal(allowed.landing, 'Punch');
});

test('manager and approval navigation requires effective permissions', () => {
  const manager = deriveMobileAccess(user('ManagerPortal', 'MANAGER', ['manager.read', 'approvals.read']));
  assert.equal(manager.surfaces.has('managerHome'), true);
  assert.equal(manager.surfaces.has('team'), true);
  assert.equal(manager.surfaces.has('approvals'), true);
  assert.equal(manager.surfaces.has('leave'), false);
});

test('payroll and finance approvers land on approvals when permission is effective', () => {
  for (const [mode, role] of [['PayrollPortal', 'PAYROLL'], ['FinancePortal', 'FINANCE_APPROVER']] as const) {
    const policy = deriveMobileAccess(user(mode, role, ['approvals.decide', 'ess.read']));
    assert.equal(policy.landing, 'Approvals');
    assert.equal(policy.surfaces.has('approvals'), true);
    assert.equal(policy.surfaces.has('payslips'), true);
  }
});

test('read-only mode never exposes write surfaces', () => {
  const policy = deriveMobileAccess(user('ReadOnlyAuditor', 'EMPLOYEE', [
    'attendance.read', 'attendance.kiosk', 'approvals.read', 'ess.write',
  ]));
  assert.equal(policy.readOnly, true);
  assert.equal(policy.surfaces.has('attendanceCorrection'), false);
  assert.equal(policy.surfaces.has('kioskPunch'), false);
  assert.equal(policy.surfaces.has('approvals'), true);
});
