export interface SessionSnapshot {
  readonly loginEpoch: number;
  readonly generation: number;
  readonly refreshToken: string | null;
}

let generation = 0;
let loginEpoch = 0;
let refreshToken: string | null = null;

export function captureSession(): SessionSnapshot {
  return { loginEpoch, generation, refreshToken };
}

export function beginSession(newRefreshToken: string): SessionSnapshot {
  loginEpoch += 1;
  generation += 1;
  refreshToken = newRefreshToken;
  return captureSession();
}

export function invalidateSession(): void {
  loginEpoch += 1;
  generation += 1;
  refreshToken = null;
}

/** Token rotation retains the signed-in identity while invalidating old writes. */
export function rotateSession(snapshot: SessionSnapshot, newRefreshToken: string): boolean {
  if (!mayCommit(snapshot, snapshot.refreshToken)) return false;
  generation += 1;
  refreshToken = newRefreshToken;
  return true;
}

export function belongsToCurrentLogin(snapshot: SessionSnapshot): boolean {
  return snapshot.loginEpoch === loginEpoch && snapshot.refreshToken !== null && refreshToken !== null;
}

export function invalidateSessionIfCurrent(snapshot: SessionSnapshot): boolean {
  if (snapshot.generation !== generation) return false;
  invalidateSession();
  return true;
}

export function mayCommit(
  snapshot: SessionSnapshot,
  currentRefreshToken: string | null,
): boolean {
  return snapshot.generation === generation
    && snapshot.refreshToken !== null
    && snapshot.refreshToken === refreshToken
    && snapshot.refreshToken === currentRefreshToken;
}
