/**
 * A signed-out/replaced session must never be restored by a late network reply.
 * Network operations capture a generation; only the current one may mutate auth.
 * Persistent writes are serialized so sign-out clears AFTER a write already in flight,
 * while stale writes that have not started are discarded. This queue never holds network I/O.
 */
let generation = 0;
let storageTail: Promise<void> = Promise.resolve();

export class SessionSupersededError extends Error {
  constructor() {
    super('The session changed while this request was in progress.');
    this.name = 'SessionSupersededError';
  }
}

export function currentSessionGeneration(): number { return generation; }
export function invalidateSession(): number { return ++generation; }
export function isCurrentSession(expected: number): boolean { return expected === generation; }
export function requireCurrentSession(expected: number): void {
  if (!isCurrentSession(expected)) throw new SessionSupersededError();
}

export function writeSession(expected: number, write: () => Promise<unknown>): Promise<boolean> {
  const result = storageTail.then(async () => {
    if (!isCurrentSession(expected)) return false;
    await write();
    return isCurrentSession(expected);
  });
  // A storage failure must not poison cleanup or the next session's writes.
  storageTail = result.then(() => undefined, () => undefined);
  return result;
}
