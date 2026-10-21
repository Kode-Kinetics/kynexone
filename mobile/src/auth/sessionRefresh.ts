import {
  captureSession,
  mayCommit,
  type SessionSnapshot,
} from './sessionGeneration';
import {
  commitRefreshSession,
  terminateSessionIfCurrent,
} from './sessionLifecycle';

export interface RefreshTokenSet {
  accessToken: string;
  refreshToken: string;
  expiresAt: number;
}

interface SessionRefreshDependencies {
  getRefreshToken: () => Promise<string | null>;
  requestRefresh: (refreshToken: string) => Promise<RefreshTokenSet>;
  saveTokens: (tokens: RefreshTokenSet) => Promise<void>;
}

interface RefreshExpirationDependencies {
  expireSession: () => Promise<void>;
}

export class StaleSessionRefreshError extends Error {}

export class OwnedSessionRefreshError extends Error {
  readonly session: SessionSnapshot;
  readonly refreshError: unknown;

  constructor(session: SessionSnapshot, refreshError: unknown) {
    super('The current session refresh failed.');
    this.session = session;
    this.refreshError = refreshError;
  }
}

function sessionStillOwnsRefresh(session: SessionSnapshot): boolean {
  return mayCommit(session, session.refreshToken);
}

export async function executeSessionRefresh(
  dependencies: SessionRefreshDependencies,
  session: SessionSnapshot = captureSession(),
): Promise<string> {
  try {
    if (!sessionStillOwnsRefresh(session)) throw new StaleSessionRefreshError();
    const refreshToken = await dependencies.getRefreshToken();
    if (!refreshToken) throw new Error('No refresh token');
    if (!mayCommit(session, refreshToken)) throw new StaleSessionRefreshError();
    const tokens = await dependencies.requestRefresh(refreshToken);
    const committed = await commitRefreshSession(
      session,
      tokens.refreshToken,
      dependencies.getRefreshToken,
      () => dependencies.saveTokens(tokens),
    );
    if (!committed) throw new StaleSessionRefreshError();
    return tokens.accessToken;
  } catch (error) {
    if (error instanceof StaleSessionRefreshError) throw error;
    if (!sessionStillOwnsRefresh(session)) {
      throw new StaleSessionRefreshError();
    }
    throw new OwnedSessionRefreshError(session, error);
  }
}

export async function expireRefreshFailureIfOwned(
  error: unknown,
  dependencies: RefreshExpirationDependencies,
): Promise<boolean> {
  if (!(error instanceof OwnedSessionRefreshError)) return false;
  return terminateSessionIfCurrent(error.session, dependencies.expireSession);
}
