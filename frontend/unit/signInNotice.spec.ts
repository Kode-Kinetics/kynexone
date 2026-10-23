import { expect, test } from '@playwright/test';
import { setSignInNotice, signInNoticeForRefreshRefusal, takeSignInNotice } from '../src/lib/signInNotice';

/**
 * The sign-in notice: which refused refresh says "Your session has ended", and that a notice is
 * shown once. Surviving the full page load to /login is a browser fact, proven in
 * e2e/two-step-admin.spec.ts.
 */

const refusal = (status: number, data: unknown) => ({ isAxiosError: true, response: { status, data } });

test('only 401 ERR_SESSION_EXPIRED from a refresh calls for the session-ended notice', () => {
  expect(signInNoticeForRefreshRefusal(refusal(401, { code: 'ERR_SESSION_EXPIRED', message: 'Your session has ended. Sign in again.' }))).toBe('session-expired');
  // Every other refusal is an ordinary sign-out, as before: no sentence.
  expect(signInNoticeForRefreshRefusal(refusal(401, { message: 'Invalid or expired refresh token.' }))).toBeNull();
  expect(signInNoticeForRefreshRefusal(refusal(401, { code: 'ERR_REFRESH_INVALID' }))).toBeNull();
  expect(signInNoticeForRefreshRefusal(refusal(401, 'ERR_SESSION_EXPIRED'))).toBeNull();
  expect(signInNoticeForRefreshRefusal(refusal(401, null))).toBeNull();
  expect(signInNoticeForRefreshRefusal(refusal(400, { code: 'ERR_SESSION_EXPIRED' }))).toBeNull();
  expect(signInNoticeForRefreshRefusal(refusal(403, { code: 'ERR_SESSION_EXPIRED' }))).toBeNull();
  expect(signInNoticeForRefreshRefusal(Object.assign(new Error('No refresh token'), { noRefreshToken: true }))).toBeNull();
  expect(signInNoticeForRefreshRefusal(null)).toBeNull();
});

test('a notice is taken once', () => {
  expect(takeSignInNotice()).toBeNull();
  setSignInNotice('two-step-on');
  expect(takeSignInNotice()).toBe('two-step-on');
  expect(takeSignInNotice()).toBeNull();
  // Without a browser there is no sessionStorage: the in-memory copy still works.
  setSignInNotice('session-expired');
  expect(takeSignInNotice()).toBe('session-expired');
});
