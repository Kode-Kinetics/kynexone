import { test, expect } from '@playwright/test';
import { afterMeFailure, afterMeSuccess, classifyMeFailure, isRefreshRefused } from '../src/lib/authLoadState';

// The reported defect: a network blip while the app loaded removed the tokens and left the user
// null, so the dashboard shell redirected to /login with no explanation. Only a 401 may end the
// session; everything else keeps it and shows "can't reach the server" with a Retry.

const axiosNetworkError = { isAxiosError: true, message: 'Network Error', code: 'ERR_NETWORK' };
const httpError = (status: number) => ({ isAxiosError: true, response: { status, data: {} } });

test('a 401 from /me is a definite sign-out and clears the session', () => {
  expect(classifyMeFailure(httpError(401))).toBe('signed-out');
  expect(afterMeFailure(httpError(401))).toEqual({ user: null, authError: null, clearSession: true });
});

test('no response at all (offline, DNS, timeout) is a network error and keeps the session', () => {
  expect(classifyMeFailure(axiosNetworkError)).toBe('network');
  expect(classifyMeFailure(new Error('socket hang up'))).toBe('network');
  expect(afterMeFailure(axiosNetworkError)).toEqual({ user: null, authError: 'network', clearSession: false });
});

test("a deploy's 502/503/504 and a 500 are server errors and keep the session", () => {
  for (const status of [500, 502, 503, 504]) {
    expect(classifyMeFailure(httpError(status))).toBe('server');
    expect(afterMeFailure(httpError(status)).clearSession).toBe(false);
  }
});

test('a 403 from /me is signed out too: a Retry would only get the same refusal', () => {
  expect(classifyMeFailure(httpError(403))).toBe('signed-out');
  expect(afterMeFailure(httpError(403))).toEqual({ user: null, authError: null, clearSession: true });
});

test("a proxy's 404 mid-deploy, a 408 or a 429 from /me keeps the session", () => {
  for (const status of [404, 408, 429]) {
    expect(classifyMeFailure(httpError(status))).toBe('server');
    expect(afterMeFailure(httpError(status)).clearSession).toBe(false);
  }
});

test('a malformed rejection value (null, a string) is treated as unreachable, never as signed out', () => {
  expect(classifyMeFailure(null)).toBe('network');
  expect(classifyMeFailure('boom')).toBe('network');
});

test('a successful /me (including a retry after an outage) clears the error', () => {
  const user = { id: 'u1' };
  expect(afterMeSuccess(user)).toEqual({ user, authError: null, clearSession: false });
});

test('a refused refresh (400/401/403, or no refresh token) ends the session; an unreachable one does not', () => {
  expect(isRefreshRefused(httpError(400))).toBe(true);
  expect(isRefreshRefused(httpError(401))).toBe(true);
  expect(isRefreshRefused(Object.assign(new Error('No refresh token'), { noRefreshToken: true }))).toBe(true);

  expect(isRefreshRefused(httpError(403))).toBe(true);

  expect(isRefreshRefused(axiosNetworkError)).toBe(false);
  expect(isRefreshRefused(httpError(502))).toBe(false);
  expect(isRefreshRefused(httpError(500))).toBe(false);
});

test('a rate-limited (429) or not-found (404) refresh does not sign the user out', () => {
  // auth_refresh is rate-limited, and a proxy can answer 404 mid-deploy: neither says the token is bad.
  expect(isRefreshRefused(httpError(429))).toBe(false);
  expect(isRefreshRefused(httpError(404))).toBe(false);
  expect(isRefreshRefused(httpError(408))).toBe(false);
});
