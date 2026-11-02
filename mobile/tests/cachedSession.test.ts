import test from 'node:test';
import assert from 'node:assert/strict';
import { parseCachedSession, toCachedSession } from '../src/storage/cachedSession.ts';

const fullUser = {
  id: 'u1', tenantId: 't1', tenantSlug: 'evostel', employeeId: 42, role: 'MANAGER',
  email: 'a@b.co', fullName: 'Ana', permissions: [{ module: 'payroll', actions: ['*'] }], department: 'Ops', managerId: 7,
};

test('only the scoping ids are cached', () => {
  const cached = toCachedSession(fullUser);
  assert.deepEqual(cached, { id: 'u1', tenantId: 't1', employeeId: '42', tenantSlug: 'evostel', role: 'MANAGER' });
  const text = JSON.stringify(cached);
  for (const leak of ['a@b.co', 'Ana', 'payroll', 'Ops']) assert.ok(!text.includes(leak));
});

test('a legacy full-profile value is cut down on read; junk reads as signed out', () => {
  assert.deepEqual(parseCachedSession(JSON.stringify(fullUser)), toCachedSession(fullUser));
  assert.equal(parseCachedSession(null), null);
  assert.equal(parseCachedSession('not json'), null);
  assert.equal(parseCachedSession('{"name":"x"}'), null);
});
