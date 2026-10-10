import test from 'node:test';
import assert from 'node:assert/strict';
import { loginErrorKey } from '../src/features/auth/loginError.ts';

const http = (status: number, data?: unknown) => ({ response: { status, data } });

test('sign-in refusals map to plain-language keys, never the raw server text', () => {
  assert.equal(loginErrorKey(http(401, { message: 'Invalid email, password, or tenant.' })), 'invalidCredentials');
  assert.equal(loginErrorKey(http(429, { error: 'account_rate_limited' })), 'tooManyTries');
  assert.equal(loginErrorKey(http(429, { error: 'ip_failure_budget' })), 'tooManyTries');
  assert.equal(loginErrorKey(http(429, { error: 'sign_in_busy' })), 'busy');
  assert.equal(loginErrorKey(http(423)), 'locked');
  assert.equal(loginErrorKey(http(403)), 'noAccess');
  assert.equal(loginErrorKey(http(502)), 'serverDown');
  assert.equal(loginErrorKey(http(400, { code: 'something' })), 'generic');
});

test('no response at all is a connection problem', () => {
  assert.equal(loginErrorKey({ code: 'ERR_NETWORK', message: 'Network Error' }), 'network');
  assert.equal(loginErrorKey({ code: 'ECONNABORTED' }), 'network');
  assert.equal(loginErrorKey(new Error('boom')), 'generic');
  assert.equal(loginErrorKey(null), 'generic');
});
