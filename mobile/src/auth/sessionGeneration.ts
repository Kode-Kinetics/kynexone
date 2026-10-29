/**
 * A signed-out/replaced session must never be restored by a late network reply.
 * Operation generations fence requests. Credential versions advance only when a
 * replacement session is fully persisted, so a failed login/MFA challenge cannot
 * cancel required sign-out cleanup. The storage queue never holds network I/O.
 */
let generation = 0;
let credentialVersion = 0;
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
export function currentCredentialVersion(): number { return credentialVersion; }
export function requireCurrentSession(expected: number): void {
  if (!isCurrentSession(expected)) throw new SessionSupersededError();
}

function enqueue<T>(operation: () => Promise<T>): Promise<T> {
  const result = storageTail.then(operation);
  // A storage failure must not poison cleanup or the next session's writes.
  storageTail = result.then(() => undefined, () => undefined);
  return result;
}

export function writeSession(expected: number, write: () => Promise<unknown>): Promise<boolean> {
  return enqueue(async () => {
    if (!isCurrentSession(expected)) return false;
    await write();
    return isCurrentSession(expected);
  });
}

export function persistNewSession(expected: number, write: () => Promise<unknown>): Promise<boolean> {
  return enqueue(async () => {
    if (!isCurrentSession(expected)) return false;
    await write();
    // A write invalidated mid-flight must still be removed by queued sign-out cleanup.
    if (!isCurrentSession(expected)) return false;
    credentialVersion++;
    return true;
  });
}

export function clearSessionCredentials(expectedVersion: number, clear: () => Promise<unknown>): Promise<boolean> {
  return enqueue(async () => {
    // Only successfully persisted replacement credentials supersede this cleanup;
    // attempted/failed logins change the operation generation, not this version.
    if (credentialVersion !== expectedVersion) return false;
    await clear();
    return true;
  });
}
