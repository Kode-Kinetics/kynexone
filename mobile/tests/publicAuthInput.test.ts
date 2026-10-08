import test from 'node:test';
import assert from 'node:assert/strict';
import {
  normalizeEmail,
  normalizeWorkspace,
  publicInvitationInput,
  publicLoginInput,
  publicResetInput,
  requireWorkspace,
} from '../src/auth/publicAuthInput.ts';

test('normalizes workspace and email identifiers only', () => {
  assert.equal(normalizeWorkspace(' \tEvOsTeL\n'), 'evostel');
  assert.equal(normalizeEmail('  Case.Sensitive@example.test \n'), 'Case.Sensitive@example.test');
  assert.throws(() => requireWorkspace(' \t\n'), /Workspace is required/);
});

test('login preserves password exactly', () => {
  const password = '  Paß word🔐  ';
  const input = publicLoginInput(' User@Example.test ', password, ' ACME ');
  assert.deepEqual(input, {
    email: 'User@Example.test',
    password,
    tenantSlug: 'acme',
  });
});

test('login without a company ID sends no tenantSlug (the server resolves it from the email)', () => {
  assert.deepEqual(publicLoginInput('a@evostel.com', 'pw', '  '), { email: 'a@evostel.com', password: 'pw' });
  assert.deepEqual(publicLoginInput('a@evostel.com', 'pw'), { email: 'a@evostel.com', password: 'pw' });
});

test('reset and invitation are token-first, contain no email, and preserve passwords', () => {
  const password = '  Exact Password!  ';
  const reset = publicResetInput('reset-secret', password, ' Demo ');
  const invitation = publicInvitationInput('invite-secret', password, ' Demo ');

  assert.deepEqual(reset, { resetToken: 'reset-secret', newPassword: password, tenantSlug: 'demo' });
  assert.deepEqual(invitation, { invitationToken: 'invite-secret', newPassword: password, tenantSlug: 'demo' });
  assert.equal('email' in reset, false);
  assert.equal('email' in invitation, false);
});
