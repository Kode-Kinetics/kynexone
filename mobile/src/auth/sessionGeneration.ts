export interface SessionSnapshot {
  readonly generation: number;
  readonly refreshToken: string | null;
}

let generation = 0;
let refreshToken: string | null = null;

export function captureSession(): SessionSnapshot {
  return { generation, refreshToken };
}

export function beginSession(newRefreshToken: string): SessionSnapshot {
  generation += 1;
  refreshToken = newRefreshToken;
  return captureSession();
}

export function invalidateSession(): void {
  generation += 1;
  refreshToken = null;
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
