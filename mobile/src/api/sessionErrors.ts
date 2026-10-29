// Pure, import-free classification of a failed refresh / session check, so the
// "is this person really signed out?" decision is testable under node --test.
//
// A session is ended ONLY by a definitive refusal from the server. A timeout,
// a dropped connection, a 5xx or a 429 says nothing about the credentials, so
// the tokens stay and the caller retries.

export type FailureKind = 'auth_refused' | 'transient';

const REFUSAL_STATUSES = new Set([401, 403, 423]);
const REFUSAL_CODES = new Set([
  'invalid_grant',
  'invalid_refresh_token',
  'refresh_token_revoked',
  'token_revoked',
  'revoked',
  'session_revoked',
  'session_expired',
]);

/** Thrown locally when there is no refresh token on the device: nothing to retry with. */
export class NoRefreshTokenError extends Error {
  constructor() {
    super('No refresh token');
    this.name = 'NoRefreshTokenError';
  }
}

export function classifyFailure(error: unknown): FailureKind {
  if (error instanceof NoRefreshTokenError) return 'auth_refused';
  if (typeof error !== 'object' || error === null) return 'transient';
  const response = (error as { response?: { status?: number; data?: unknown } }).response;
  if (!response || typeof response.status !== 'number') return 'transient'; // timeout, offline, DNS, aborted
  if (REFUSAL_STATUSES.has(response.status)) return 'auth_refused';
  if (response.status === 400) {
    const data = response.data as { code?: unknown; error?: unknown } | undefined;
    const code = String(data?.code ?? data?.error ?? '').toLowerCase();
    if (REFUSAL_CODES.has(code)) return 'auth_refused';
  }
  return 'transient'; // 408, 429, 5xx, and any other 4xx that is not an auth verdict
}

export const isDefinitiveAuthRefusal = (error: unknown): boolean => classifyFailure(error) === 'auth_refused';

/** Exponential backoff with a ceiling: 1s, 2s, 4s, ... up to maxMs. `jitter` in [0,1) trims up to 25%. */
export function backoffDelayMs(attempt: number, jitter = 0, baseMs = 1000, maxMs = 30_000): number {
  const raw = Math.min(maxMs, baseMs * 2 ** Math.max(0, attempt));
  return Math.round(raw * (1 - 0.25 * Math.min(Math.max(jitter, 0), 1)));
}

/**
 * Runs `task`, retrying transient failures up to `retries` more times with backoff.
 * A definitive auth refusal is never retried; the last error is rethrown.
 */
export async function retryTransient<T>(
  task: () => Promise<T>,
  options: { retries: number; sleep: (ms: number) => Promise<void>; jitter?: () => number },
): Promise<T> {
  let attempt = 0;
  for (;;) {
    try {
      return await task();
    } catch (error) {
      if (classifyFailure(error) !== 'transient' || attempt >= options.retries) throw error;
      await options.sleep(backoffDelayMs(attempt, options.jitter?.() ?? 0));
      attempt += 1;
    }
  }
}

export type ErrorMessageKey =
  | 'safety.errors.forbidden'
  | 'safety.errors.locked'
  | 'safety.errors.offline'
  | 'safety.errors.timeout'
  | 'safety.errors.server';

/**
 * The i18n key for errors the app words itself (permission, lock, connectivity).
 * Returns null when the server's own message should be shown instead.
 */
export function localizedErrorKey(error: unknown): ErrorMessageKey | null {
  if (typeof error !== 'object' || error === null) return null;
  const { response, code, message } = error as {
    response?: { status?: number; data?: { message?: unknown } };
    code?: string;
    message?: string;
  };
  if (!response || typeof response.status !== 'number') {
    if (code === 'ECONNABORTED' || code === 'ETIMEDOUT' || /timeout/i.test(String(message))) return 'safety.errors.timeout';
    if (code === 'ERR_NETWORK' || /network error/i.test(String(message))) return 'safety.errors.offline';
    return null;
  }
  if (response.status === 403) return 'safety.errors.forbidden';
  if (response.status === 423) return 'safety.errors.locked';
  const hasServerText = typeof response.data?.message === 'string' && response.data.message.length > 0;
  if (response.status >= 500 && !hasServerText) return 'safety.errors.server';
  return null;
}

export const MAX_GET_RETRIES = 2;

/**
 * Only idempotent reads are replayed automatically, and only for failures that say nothing about
 * the request itself: no response (offline/timeout) or a gateway-class 502/503/504. Writes are
 * never replayed: a punch or a loan application must not be sent twice.
 */
export function shouldRetryGet(method: string | undefined, error: unknown, attempt: number): boolean {
  if ((method ?? 'get').toLowerCase() !== 'get' || attempt >= MAX_GET_RETRIES) return false;
  if (typeof error !== 'object' || error === null) return false;
  const response = (error as { response?: { status?: number } }).response;
  if (!response || typeof response.status !== 'number') return true;
  return response.status === 502 || response.status === 503 || response.status === 504;
}
