import test from 'node:test';
import assert from 'node:assert/strict';
import {
  normalizeEmail,
  publicInvitationInput,
  publicLoginInput,
  publicResetInput,
} from '../src/auth/publicAuthInput.ts';

test('normalizes the email only', () => {
  assert.equal(normalizeEmail('  Case.Sensitive@example.test \n'), 'Case.Sensitive@example.test');
});

test('login is email and password, and preserves the password exactly', () => {
  const password = '  Paß word🔐  ';
  assert.deepEqual(publicLoginInput(' User@Example.test ', password), { email: 'User@Example.test', password });
  assert.throws(() => publicLoginInput('  ', password), /Work email is required/);
});

test('reset and invitation are token-first, contain no email or company ID, and preserve passwords', () => {
  const password = '  Exact Password!  ';
  const reset = publicResetInput('reset-secret', password);
  const invitation = publicInvitationInput('invite-secret', password);

  assert.deepEqual(reset, { resetToken: 'reset-secret', newPassword: password });
  assert.deepEqual(invitation, { invitationToken: 'invite-secret', newPassword: password });
  assert.equal('email' in reset, false);
  assert.equal('email' in invitation, false);
});
