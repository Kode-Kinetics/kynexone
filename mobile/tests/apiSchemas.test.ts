import test from 'node:test';
import assert from 'node:assert/strict';
import {
  ROUTE_SCHEMAS,
  ResponseShapeError,
  findRouteSchema,
  normalizeRequestPath,
  validateRefreshResponse,
  validateResponse,
} from '../src/api/schemas.ts';

test('request paths are normalised before matching', () => {
  assert.equal(normalizeRequestPath('/ess/payslips?x=1'), '/ess/payslips');
  assert.equal(normalizeRequestPath('https://h.example/api/ess/payslips/'), '/ess/payslips');
  assert.equal(findRouteSchema('get', '/ess/payslips/42')?.name, 'ess.payslipDetail');
  assert.equal(findRouteSchema('GET', '/ess/payslips/42/deductions')?.name, 'ess.payslipDeductions');
  assert.equal(findRouteSchema('GET', '/leave/balances/employee/7')?.name, 'leave.balancesForEmployee');
  assert.equal(findRouteSchema('GET', '/not/a/route'), undefined);
});

test('the route table has no duplicate method+pattern entries', () => {
  const keys = ROUTE_SCHEMAS.map((r) => `${r.method} ${r.pattern.source}`);
  assert.equal(new Set(keys).size, keys.length);
});

test('good payslip, balance, approval, loan and notification replies pass untouched', () => {
  const slips = [{ id: 1, month: 9, year: 2026, netSalary: '9500.50', grossSalary: 10000, currency: 'SAR', extra: { a: 1 } }];
  assert.equal(validateResponse('GET', '/ess/payslips', slips), slips);
  const bal = [{ leaveTypeId: 3, allocated: 21, used: '4', pending: null, available: 17 }];
  assert.equal(validateResponse('GET', '/leave/balances/employee/9', bal), bal);
  assert.doesNotThrow(() => validateResponse('GET', '/approval-requests?status=Pending&page=1', { items: [{ id: 'a', status: 'Pending' }], total: 1 }));
  assert.doesNotThrow(() => validateResponse('GET', '/approval-requests', [{ id: 1 }]));
  assert.doesNotThrow(() => validateResponse('GET', '/finance/loans?mine=true', { total: 0, items: [] }));
  assert.doesNotThrow(() => validateResponse('GET', '/mobile/notifications/5', []));
  assert.doesNotThrow(() => validateResponse('POST', '/leave/requests/5/cancel', ''));
});

test('a missing optional field is not a mismatch', () => {
  assert.doesNotThrow(() => validateResponse('GET', '/ess/payslips', [{}]));
  assert.doesNotThrow(() => validateResponse('GET', '/auth/me', { email: 'a@b.c' }));
});

test('wrong containers and unusable money fail with a ResponseShapeError that carries no values', () => {
  for (const [url, bad] of [
    ['/ess/payslips', { message: 'oops' }],
    ['/ess/payslips', '<html>Bad gateway</html>'],
    ['/ess/payslips', [{ netSalary: 'N/A' }]],
    ['/leave/balances/employee/1', [{ available: { days: 3 } }]],
    ['/finance/loans', [{ id: 1 }]],
    ['/finance/loans', { items: [{ outstandingBalance: 'secret-12,5' }] }],
    ['/auth/me', 'nope'],
  ] as const) {
    assert.throws(() => validateResponse('GET', url, bad), (e: unknown) => {
      assert.ok(e instanceof ResponseShapeError);
      assert.ok(!JSON.stringify(e.issues).includes('secret'));
      return true;
    });
  }
});

test('refresh replies need real tokens', () => {
  assert.equal(validateRefreshResponse({ accessToken: 'a', refreshToken: 'r', expiresAtUtc: '2026-01-01T00:00:00Z' }).accessToken, 'a');
  assert.throws(() => validateRefreshResponse({ accessToken: '', refreshToken: 'r' }), ResponseShapeError);
  assert.throws(() => validateRefreshResponse(null), ResponseShapeError);
});

test('a refresh reply without an expiry is rejected (it would store a NaN expiry)', () => {
  assert.throws(() => validateRefreshResponse({ accessToken: 'a', refreshToken: 'r' }), ResponseShapeError);
});
