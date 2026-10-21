import {
  beginSession,
  invalidateSession,
  invalidateSessionIfCurrent,
  mayCommit,
  rotateSession,
  type SessionSnapshot,
} from './sessionGeneration';

let persistenceTail: Promise<void> = Promise.resolve();

function serializePersistence<T>(operation: () => Promise<T>): Promise<T> {
  const result = persistenceTail.then(operation, operation);
  persistenceTail = result.then(
    () => undefined,
    () => undefined,
  );
  return result;
}

export async function commitRefreshSession(
  snapshot: SessionSnapshot,
  newRefreshToken: string,
  getCurrentRefreshToken: () => Promise<string | null>,
  persist: () => Promise<void>,
): Promise<boolean> {
  return serializePersistence(async () => {
    if (!mayCommit(snapshot, await getCurrentRefreshToken())) return false;
    await persist();
    if (!mayCommit(snapshot, snapshot.refreshToken)) return false;
    return rotateSession(snapshot, newRefreshToken);
  });
}

export async function establishSession(
  refreshToken: string,
  persist: () => Promise<void>,
): Promise<void> {
  beginSession(refreshToken);
  await serializePersistence(persist);
}

export function terminateSession<T>(teardown: () => Promise<T>): Promise<T> {
  invalidateSession();
  return serializePersistence(teardown);
}

export function terminateSessionIfCurrent(
  snapshot: SessionSnapshot,
  teardown: () => Promise<void>,
): Promise<boolean> {
  if (!invalidateSessionIfCurrent(snapshot)) return Promise.resolve(false);
  return serializePersistence(teardown).then(() => true);
}
