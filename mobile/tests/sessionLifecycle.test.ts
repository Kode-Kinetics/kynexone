import test from 'node:test';
import assert from 'node:assert/strict';
import {
  beginSession,
  captureSession,
  invalidateSession,
  mayCommit,
  type SessionSnapshot,
} from '../src/auth/sessionGeneration.ts';

interface Tokens {
  accessToken: string;
  refreshToken: string;
}

function commitRefresh(
  snapshot: SessionSnapshot,
  currentRefreshToken: string | null,
  response: Tokens,
  committed: Tokens[],
): void {
  if (mayCommit(snapshot, currentRefreshToken)) committed.push(response);
}

test('a refresh response arriving after logout cannot restore tokens', () => {
  const committed: Tokens[] = [];
  beginSession('refresh-a');
  const refreshRequest = captureSession();

  invalidateSession();
  commitRefresh(refreshRequest, null, { accessToken: 'late-access', refreshToken: 'late-refresh' }, committed);

  assert.deepEqual(committed, []);
});

test('a refresh response from an older login cannot overwrite newer login tokens', () => {
  const committed: Tokens[] = [];
  beginSession('refresh-a');
  const refreshRequest = captureSession();

  beginSession('refresh-b');
  commitRefresh(refreshRequest, 'refresh-b', { accessToken: 'late-access', refreshToken: 'late-refresh' }, committed);

  assert.deepEqual(committed, []);
});

test('a refresh response for the current login can commit', () => {
  const committed: Tokens[] = [];
  beginSession('refresh-a');
  const refreshRequest = captureSession();
  const response = { accessToken: 'fresh-access', refreshToken: 'fresh-refresh' };

  commitRefresh(refreshRequest, 'refresh-a', response, committed);

  assert.deepEqual(committed, [response]);
});
