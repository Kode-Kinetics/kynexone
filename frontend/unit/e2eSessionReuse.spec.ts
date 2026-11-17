import { test, expect } from '@playwright/test';
import { liveSession, secondsUntilExpiry, TOKEN_MARGIN_SECONDS, type TenantSession } from '../e2e/sessionReuse';

/**
 * The e2e helpers reuse the session minted at setup only while its access token has more than the margin left; then the
 * same persona signs in again, once per token life (group-company specs got 401 for an expired setup token, CI #351).
 */
const jwt = (secondsLeft: number) => {
  const b64 = (o: object) => Buffer.from(JSON.stringify(o)).toString('base64').replace(/=+$/, '').replace(/\+/g, '-').replace(/\//g, '_');
  return `${b64({ alg: 'none' })}.${b64({ exp: Math.floor(Date.now() / 1000) + secondsLeft })}.sig`;
};
const session = (secondsLeft: number, tag: string): TenantSession => ({ accessToken: jwt(secondsLeft), refreshToken: tag });

test('reads the seconds left from the token', () => {
  expect(Math.round(secondsUntilExpiry(jwt(600)))).toBeGreaterThanOrEqual(599);
  expect(secondsUntilExpiry('not-a-jwt')).toBe(Infinity);
});

test('a setup session with more than the margin left is reused, and nobody signs in', async () => {
  let mints = 0;
  const s = await liveSession(session(TOKEN_MARGIN_SECONDS + 120, 'setup'), 'k', async () => { mints++; return session(1800, 'fresh'); }, new Map());
  expect(s.refreshToken).toBe('setup');
  expect(mints).toBe(0);
});

test('an expired (or nearly expired) setup session is replaced by ONE fresh sign-in, then the fresh one is reused', async () => {
  const cache = new Map<string, TenantSession>();
  let mints = 0;
  const mint = async () => { mints++; return session(1800, `fresh-${mints}`); };
  for (const left of [-600, 0, TOKEN_MARGIN_SECONDS]) {
    const s = await liveSession(session(left, 'setup'), 'k', mint, cache);
    expect(s.refreshToken).toBe('fresh-1');
  }
  expect(mints).toBe(1);
});

test('a cached fresh session that is itself near expiry is replaced', async () => {
  const cache = new Map<string, TenantSession>([['k', session(60, 'old-fresh')]]);
  const s = await liveSession(session(-1, 'setup'), 'k', async () => session(1800, 'new-fresh'), cache);
  expect(s.refreshToken).toBe('new-fresh');
  expect(cache.get('k')!.refreshToken).toBe('new-fresh');
});

test('each persona has its own fresh session', async () => {
  const cache = new Map<string, TenantSession>();
  const a = await liveSession(session(-1, 's'), 'a', async () => session(1800, 'A'), cache);
  const b = await liveSession(session(-1, 's'), 'b', async () => session(1800, 'B'), cache);
  expect([a.refreshToken, b.refreshToken]).toEqual(['A', 'B']);
});
