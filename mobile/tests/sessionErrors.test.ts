import test from 'node:test';
import assert from 'node:assert/strict';
import {
  NoRefreshTokenError,
  backoffDelayMs,
  classifyFailure,
  isDefinitiveAuthRefusal,
  localizedErrorKey,
  retryTransient,
  shouldRetryGet,
} from '../src/api/sessionErrors.ts';

const http = (status: number, data?: unknown) => ({ response: { status, data } });

test('only a definitive auth refusal ends the session', () => {
  for (const status of [401, 403, 423]) assert.equal(classifyFailure(http(status)), 'auth_refused');
  assert.equal(classifyFailure(http(400, { code: 'invalid_grant' })), 'auth_refused');
  assert.equal(classifyFailure(http(400, { error: 'Revoked' })), 'auth_refused');
  assert.equal(classifyFailure(new NoRefreshTokenError()), 'auth_refused');
});

test('timeouts, offline, 5xx, 429 and plain 400s keep the session', () => {
  assert.equal(classifyFailure(Object.assign(new Error('timeout of 10000ms exceeded'), { code: 'ECONNABORTED' })), 'transient');
  assert.equal(classifyFailure(new Error('Network Error')), 'transient');
  for (const status of [400, 404, 408, 429, 500, 502, 503, 504]) assert.equal(classifyFailure(http(status)), 'transient');
  assert.equal(classifyFailure(undefined), 'transient');
  assert.equal(isDefinitiveAuthRefusal(http(502)), false);
});

test('backoff doubles and is capped', () => {
  assert.deepEqual([0, 1, 2, 3].map((n) => backoffDelayMs(n)), [1000, 2000, 4000, 8000]);
  assert.equal(backoffDelayMs(20), 30_000);
  assert.equal(backoffDelayMs(1, 1), 1500);
});

test('retryTransient retries transient failures then succeeds', async () => {
  const waits: number[] = [];
  let calls = 0;
  const result = await retryTransient(
    async () => {
      calls += 1;
      if (calls < 3) throw new Error('Network Error');
      return 'ok';
    },
    { retries: 3, sleep: async (ms) => void waits.push(ms) },
  );
  assert.equal(result, 'ok');
  assert.deepEqual(waits, [1000, 2000]);
});

test('retryTransient never retries a refusal and gives up after the retry budget', async () => {
  let calls = 0;
  await assert.rejects(retryTransient(async () => { calls += 1; throw http(401); }, { retries: 3, sleep: async () => {} }));
  assert.equal(calls, 1);
  calls = 0;
  await assert.rejects(retryTransient(async () => { calls += 1; throw http(503); }, { retries: 2, sleep: async () => {} }));
  assert.equal(calls, 3);
});

test('app-worded errors map to i18n keys; server text wins otherwise', () => {
  assert.equal(localizedErrorKey(http(403)), 'safety.errors.forbidden');
  assert.equal(localizedErrorKey(http(423)), 'safety.errors.locked');
  assert.equal(localizedErrorKey(new Error('Network Error')), 'safety.errors.offline');
  assert.equal(localizedErrorKey(Object.assign(new Error('x'), { code: 'ECONNABORTED' })), 'safety.errors.timeout');
  assert.equal(localizedErrorKey(http(503)), 'safety.errors.server');
  assert.equal(localizedErrorKey(http(503, { message: 'Down for maintenance' })), null);
  assert.equal(localizedErrorKey(http(400, { message: 'Bad' })), null);
});

test('only reads are replayed, and only for connectivity or gateway failures', () => {
  assert.equal(shouldRetryGet('get', new Error('Network Error'), 0), true);
  assert.equal(shouldRetryGet('GET', http(503), 1), true);
  assert.equal(shouldRetryGet('get', http(503), 2), false);
  assert.equal(shouldRetryGet('get', http(500), 0), false);
  assert.equal(shouldRetryGet('get', http(404), 0), false);
  assert.equal(shouldRetryGet('post', new Error('Network Error'), 0), false);
  assert.equal(shouldRetryGet('put', http(503), 0), false);
});
