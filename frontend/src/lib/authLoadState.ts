/**
 * What a failed session check means for the signed-in user.
 *
 * WHY. On load the app asks `/api/auth/me` who the user is. Before this, ANY failure — a dropped
 * connection, a deploy's ~38 s of 502s, a cold start — removed the tokens and left the user null,
 * so the dashboard shell redirected to /login with no explanation: a network blip signed people out.
 *
 * Only a definite "not signed in" (401, after the API client's own refresh attempt) ends the
 * session. Anything else keeps the tokens and is shown as "can't reach the server right now" with
 * a Retry, so the user loses nothing and is never sent to the login page by an outage.
 */

/** Why the session could not be confirmed, when it was NOT because the user is signed out. */
export type AuthLoadError = 'network' | 'server';

export type MeFailureOutcome = 'signed-out' | AuthLoadError;

interface HttpFailure {
  response?: { status?: number };
}

function statusOf(err: unknown): number | undefined {
  const status = (err as HttpFailure | null)?.response?.status;
  return typeof status === 'number' ? status : undefined;
}

/**
 * Classify a failed `/me` call.
 *  - 401  → 'signed-out' (the session is over; clear it as before);
 *  - no HTTP response at all (offline, DNS, CORS, timeout, refresh could not be reached) → 'network';
 *  - any other status (5xx, a proxy's 404 during a deploy, an unexpected 4xx) → 'server'.
 */
export function classifyMeFailure(err: unknown): MeFailureOutcome {
  const status = statusOf(err);
  if (status === 401) return 'signed-out';
  if (status === undefined) return 'network';
  return 'server';
}

/**
 * Did the token refresh fail because the server REFUSED the refresh token (the session is truly
 * over), rather than because the server could not be reached or errored?
 *
 * A missing refresh token, or a 4xx answer from /api/auth/refresh, is a definite refusal. A network
 * error or a 5xx is not: the refresh token may well still be valid, so the session must be kept.
 */
export function isRefreshRefused(err: unknown): boolean {
  if ((err as { noRefreshToken?: boolean } | null)?.noRefreshToken) return true;
  const status = statusOf(err);
  return status !== undefined && status >= 400 && status < 500;
}

/** The auth state after a `/me` attempt, as a pure transition (see AuthContext). */
export interface AuthLoadState<U> {
  user: U | null;
  authError: AuthLoadError | null;
  /** True when the stored tokens must be removed (the session is definitely over). */
  clearSession: boolean;
}

export function afterMeSuccess<U>(user: U): AuthLoadState<U> {
  return { user, authError: null, clearSession: false };
}

export function afterMeFailure<U>(err: unknown): AuthLoadState<U> {
  const outcome = classifyMeFailure(err);
  if (outcome === 'signed-out') return { user: null, authError: null, clearSession: true };
  return { user: null, authError: outcome, clearSession: false };
}
