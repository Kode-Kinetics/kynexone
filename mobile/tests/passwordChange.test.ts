import test from 'node:test';
import assert from 'node:assert/strict';
import { afterPasswordSaved, classifyResetFailure, validateNewPassword } from '../src/auth/passwordChange.ts';

test('validation: length first, then match', () => {
  assert.equal(validateNewPassword('short', 'short'), 'tooShort');
  assert.equal(validateNewPassword('123456789', '123456789'), 'tooShort');
  assert.equal(validateNewPassword('1234567890', 'different12'), 'mismatch');
  assert.equal(validateNewPassword('1234567890', '1234567890'), null);
});

const http = (status: number, data?: unknown) => ({ isAxiosError: true, response: { status, data } });

test('reset failures: 401 expired, 400 shows server message, others retry', () => {
  assert.deepEqual(classifyResetFailure(http(401)), { kind: 'expired' });
  assert.deepEqual(classifyResetFailure(http(400, { message: 'Too common.' })), { kind: 'rejected', message: 'Too common.' });
  assert.deepEqual(classifyResetFailure(http(400, {})), { kind: 'rejected', message: null });
  assert.deepEqual(classifyResetFailure(http(429)), { kind: 'retry', reason: 'rateLimited' });
  assert.deepEqual(classifyResetFailure(http(503)), { kind: 'retry', reason: 'server' });
  assert.deepEqual(classifyResetFailure({ isAxiosError: true }), { kind: 'retry', reason: 'network' });
});

test('after the password is saved, a failed or unusable re-login returns to sign-in', () => {
  assert.equal(afterPasswordSaved({ ok: false }), 'signInWithNotice');
  assert.equal(afterPasswordSaved({ ok: true, kind: 'passwordChange' }), 'signInWithNotice');
  assert.equal(afterPasswordSaved({ ok: true, kind: 'authenticated' }), 'continue');
  assert.equal(afterPasswordSaved({ ok: true, kind: 'mfaChallenge' }), 'continue');
});
