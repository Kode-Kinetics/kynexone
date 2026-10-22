import test from 'node:test';
import assert from 'node:assert/strict';
import { registerHooks } from 'node:module';
import {
  beginSession,
  captureSession,
  mayCommit,
} from '../src/auth/sessionGeneration.ts';

registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier.startsWith('.') && !specifier.endsWith('.ts')) {
      return nextResolve(`${specifier}.ts`, context);
    }
    return nextResolve(specifier, context);
  },
});

const lifecycle = await import('../src/auth/sessionLifecycle.ts');
const {
  commitRefreshSession,
  establishSession,
  terminateSession,
  terminateSessionIfCurrent,
} = lifecycle;

function deferred<T = void>() {
  let resolve!: (value: T | PromiseLike<T>) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<T>((resolvePromise, rejectPromise) => {
    resolve = resolvePromise;
    reject = rejectPromise;
  });
  return { promise, resolve, reject };
}

test('exposes atomic ownership check and invalidation for expiration', () => {
  assert.equal(typeof (lifecycle as Record<string, unknown>).terminateSessionIfCurrent, 'function');
});

test('conditional expiration invalidates before a newer login can queue persistence', async () => {
  let storedRefreshToken: string | null = 'refresh-a';
  const refresh = beginSession('refresh-a');
  const teardownStarted = deferred();
  const releaseTeardown = deferred();

  const expiration = terminateSessionIfCurrent(refresh, async () => {
    teardownStarted.resolve();
    await releaseTeardown.promise;
    storedRefreshToken = null;
  });
  assert.equal(mayCommit(refresh, 'refresh-a'), false);
  await teardownStarted.promise;

  const newLogin = establishSession('refresh-b', async () => {
    storedRefreshToken = 'refresh-b';
  });
  releaseTeardown.resolve();

  assert.equal(await expiration, true);
  await newLogin;
  assert.equal(storedRefreshToken, 'refresh-b');
  assert.equal(mayCommit(captureSession(), 'refresh-b'), true);
});

test('conditional expiration cannot invalidate a session that already changed', async () => {
  const refresh = beginSession('refresh-a');
  const current = beginSession('refresh-b');
  let teardowns = 0;

  const expired = await terminateSessionIfCurrent(refresh, async () => {
    teardowns += 1;
  });

  assert.equal(expired, false);
  assert.equal(teardowns, 0);
  assert.equal(mayCommit(current, 'refresh-b'), true);
});

test('a newer login persists after an already-scheduled logout teardown', async () => {
  let storedRefreshToken: string | null = 'refresh-a';
  beginSession('refresh-a');
  const teardownStarted = deferred();
  const releaseTeardown = deferred();

  const logout = terminateSession(async () => {
    teardownStarted.resolve();
    await releaseTeardown.promise;
    storedRefreshToken = null;
  });
  await teardownStarted.promise;
  const newLogin = establishSession('refresh-b', async () => {
    storedRefreshToken = 'refresh-b';
  });
  releaseTeardown.resolve();

  await logout;
  await newLogin;
  assert.equal(storedRefreshToken, 'refresh-b');
  assert.equal(mayCommit(captureSession(), 'refresh-b'), true);
});

test('a newer login wins when an old refresh is already persisting', async () => {
  let storedRefreshToken: string | null = 'refresh-a';
  beginSession('refresh-a');
  const oldRefresh = captureSession();
  const persistenceStarted = deferred();
  const releasePersistence = deferred();

  const oldCommit = commitRefreshSession(
    oldRefresh,
    'refresh-a-rotated',
    async () => storedRefreshToken,
    async () => {
      persistenceStarted.resolve();
      await releasePersistence.promise;
      storedRefreshToken = 'refresh-a-rotated';
    },
  );
  await persistenceStarted.promise;

  const newLogin = establishSession('refresh-b', async () => {
    storedRefreshToken = 'refresh-b';
  });
  releasePersistence.resolve();

  assert.equal(await oldCommit, false);
  await newLogin;
  assert.equal(storedRefreshToken, 'refresh-b');
  assert.equal(mayCommit(captureSession(), 'refresh-b'), true);
});

test('logout cleanup runs after pending refresh persistence and leaves no tokens', async () => {
  let storedRefreshToken: string | null = 'refresh-a';
  beginSession('refresh-a');
  const oldRefresh = captureSession();
  const persistenceStarted = deferred();
  const releasePersistence = deferred();

  const oldCommit = commitRefreshSession(
    oldRefresh,
    'refresh-a-rotated',
    async () => storedRefreshToken,
    async () => {
      persistenceStarted.resolve();
      await releasePersistence.promise;
      storedRefreshToken = 'refresh-a-rotated';
    },
  );
  await persistenceStarted.promise;

  const logoutCleanup = terminateSession(async () => {
    storedRefreshToken = null;
  });
  releasePersistence.resolve();

  assert.equal(await oldCommit, false);
  await logoutCleanup;
  assert.equal(storedRefreshToken, null);
  assert.equal(mayCommit(oldRefresh, 'refresh-a-rotated'), false);
});

test('same-generation refresh persists, rotates the session, and may replay', async () => {
  let storedRefreshToken: string | null = 'refresh-a';
  const refresh = beginSession('refresh-a');

  const committed = await commitRefreshSession(
    refresh,
    'refresh-a-rotated',
    async () => storedRefreshToken,
    async () => {
      storedRefreshToken = 'refresh-a-rotated';
    },
  );

  assert.equal(committed, true);
  assert.equal(mayCommit(captureSession(), 'refresh-a-rotated'), true);
});
