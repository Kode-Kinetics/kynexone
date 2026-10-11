import test from 'node:test';
import assert from 'node:assert/strict';
import { deriveMobileAccess } from '../src/auth/accessPolicy.ts';
import { resolveRouteFor, type AppRoute } from '../src/navigation/routeResolver.ts';
import type { AccessMode, AuthUser } from '../src/types/index.ts';

function user(mode: AccessMode, role: AuthUser['role'], permissions: string[]): AuthUser {
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

const employee = user('FullPortal', 'EMPLOYEE', ['ess.read', 'ess.write', 'loans.self']);
const manager = user('ManagerPortal', 'MANAGER', ['ess.read', 'ess.write', 'manager.read', 'approvals.decide']);
const payroll = user('PayrollPortal', 'PAYROLL', ['ess.read', 'approvals.decide']);

const resolve = (route: AppRoute, who: AuthUser, params?: Record<string, unknown>) =>
  resolveRouteFor(route, params, who, deriveMobileAccess(who));

test('employee: ApplyLeave keeps its segment param on the Leave tab', () => {
  assert.deepEqual(resolve('ApplyLeave', employee, { segment: 'history' }), { name: 'Leave', params: { segment: 'history' } });
});

test('manager and specialist: ApplyLeave opens in More, params included', () => {
  for (const who of [manager, payroll]) {
    assert.deepEqual(resolve('ApplyLeave', who, { segment: 'request' }), {
      name: 'More', params: { screen: 'ApplyLeave', params: { segment: 'request' }, initial: false },
    });
  }
});

test('manager: Team keeps its segment param; an employee without Team goes to Account', () => {
  assert.deepEqual(resolve('Team', manager, { segment: 'whosOut' }), { name: 'Team', params: { segment: 'whosOut' } });
  assert.deepEqual(resolve('Team', employee, { segment: 'today' }), { name: 'More', params: { screen: 'Account', params: undefined, initial: false } });
});

test('Letters, Loans and Profile resolve into the More stack for an employee', () => {
  for (const route of ['Letters', 'Loans', 'Profile'] as const) {
    assert.deepEqual(resolve(route, employee), { name: 'More', params: { screen: route, params: undefined, initial: false } });
  }
  assert.deepEqual(resolve('Letters', manager).params, { screen: 'Letters', params: undefined, initial: false });
});

test('Loans without a loans permission, and the AI assistant while off, fall back to Account', () => {
  assert.deepEqual(resolve('Loans', manager), { name: 'More', params: { screen: 'Account', params: undefined, initial: false } });
  assert.deepEqual(resolve('AIAssistant', employee), { name: 'More', params: { screen: 'Account', params: undefined, initial: false } });
});

test('employee payslips keep params on the Payslips tab stack', () => {
  assert.deepEqual(resolve('Payslips', employee, { month: '2026-09' }), {
    name: 'Payslips', params: { screen: 'PayslipsList', params: { month: '2026-09' } },
  });
});
