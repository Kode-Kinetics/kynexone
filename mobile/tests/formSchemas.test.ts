import test from 'node:test';
import assert from 'node:assert/strict';
import { loginSchema, makeChangePasswordSchema } from '../src/auth/formSchemas.ts';

// The messages are translation keys; the screens look them up, so a changed key is a blank
// error label. These pin the exact key and the field it lands on, per zod's first issue per path
// (which is what @hookform/resolvers shows).
type Issue = { path: PropertyKey[]; message: string };
const firstPerPath = (result: { success: boolean; error?: { issues: Issue[] } }): Record<string, string> => {
  const out: Record<string, string> = {};
  for (const issue of result.error?.issues ?? []) {
    const key = issue.path.join('.');
    if (!(key in out)) out[key] = issue.message;
  }
  return out;
};

test('login: a filled form is valid, including an empty company id', () => {
  assert.equal(loginSchema.safeParse({ tenantId: '', username: 'a@b.sa', password: 'x' }).success, true);
  assert.equal(loginSchema.safeParse({ tenantId: 'acme', username: ' a@b.sa ', password: 'x' }).success, true);
});

test('login: blank or whitespace username and empty password carry their translation keys', () => {
  assert.deepEqual(firstPerPath(loginSchema.safeParse({ tenantId: '', username: '', password: 'x' })), { username: 'signin.emailRequired' });
  assert.deepEqual(firstPerPath(loginSchema.safeParse({ tenantId: '', username: '   ', password: 'x' })), { username: 'signin.emailRequired' });
  assert.deepEqual(firstPerPath(loginSchema.safeParse({ tenantId: '', username: 'a@b.sa', password: '' })), { password: 'signin.passwordRequired' });
  assert.deepEqual(
    firstPerPath(loginSchema.safeParse({ tenantId: '', username: '', password: '' })),
    { username: 'signin.emailRequired', password: 'signin.passwordRequired' },
  );
});

test('login: the company id is not validated here, but it must be a string', () => {
  assert.equal(loginSchema.safeParse({ username: 'a@b.sa', password: 'x' }).success, false);
  assert.equal(loginSchema.safeParse({ tenantId: 7, username: 'a@b.sa', password: 'x' }).success, false);
});

const pw = makeChangePasswordSchema(10);

test('change password: a long enough matching new password is valid', () => {
  assert.equal(pw.safeParse({ currentPassword: 'old', newPassword: '1234567890', confirmPassword: '1234567890' }).success, true);
});

test('change password: each rule maps to its key on its own field', () => {
  assert.deepEqual(
    firstPerPath(pw.safeParse({ currentPassword: '', newPassword: '1234567890', confirmPassword: '1234567890' })),
    { currentPassword: 'currentRequired' },
  );
  assert.deepEqual(
    firstPerPath(pw.safeParse({ currentPassword: 'old', newPassword: '123456789', confirmPassword: '123456789' })),
    { newPassword: 'tooShort' },
  );
  assert.deepEqual(
    firstPerPath(pw.safeParse({ currentPassword: 'old', newPassword: '1234567890', confirmPassword: '' })),
    { confirmPassword: 'confirmRequired' },
  );
  assert.deepEqual(
    firstPerPath(pw.safeParse({ currentPassword: 'old', newPassword: '1234567890', confirmPassword: 'different12' })),
    { confirmPassword: 'mismatch' },
  );
});

test('change password: the minimum is the one the caller passes', () => {
  const twelve = makeChangePasswordSchema(12);
  assert.deepEqual(
    firstPerPath(twelve.safeParse({ currentPassword: 'old', newPassword: '1234567890', confirmPassword: '1234567890' })),
    { newPassword: 'tooShort' },
  );
});

test('change password: empty form reports the three required keys', () => {
  const got = firstPerPath(pw.safeParse({ currentPassword: '', newPassword: '', confirmPassword: '' }));
  assert.equal(got.currentPassword, 'currentRequired');
  assert.equal(got.newPassword, 'tooShort');
  assert.equal(got.confirmPassword, 'confirmRequired');
});
