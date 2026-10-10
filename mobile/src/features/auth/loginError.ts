// Plain-language sign-in errors. The server's own message is English and sometimes technical
// ("Invalid email, password, or tenant."), so the sign-in screen never shows it: it shows one of
// these keys, translated (account.login.errors.*). Import-free so node --test can load it.

export type LoginErrorKey =
  | 'invalidCredentials'
  | 'tooManyTries'
  | 'busy'
  | 'locked'
  | 'noAccess'
  | 'serverDown'
  | 'network'
  | 'generic';

interface ErrorShape {
  response?: { status?: number; data?: unknown };
  code?: unknown;
  message?: unknown;
}

function field(data: unknown, key: string): string | undefined {
  if (!data || typeof data !== 'object') return undefined;
  const value = (data as Record<string, unknown>)[key];
  return typeof value === 'string' ? value : undefined;
}

/** Maps a failed POST /auth/login to the message the person should read. */
export function loginErrorKey(error: unknown): LoginErrorKey {
  if (!error || typeof error !== 'object') return 'generic';
  const e = error as ErrorShape;
  const status = e.response?.status;
  if (status === undefined) {
    // No response at all: offline, DNS, timeout (axios ECONNABORTED / ERR_NETWORK).
    return e.code === 'ECONNABORTED' || e.code === 'ERR_NETWORK' || e.message === 'Network Error' || 'response' in e
      ? 'network'
      : 'generic';
  }
  const code = field(e.response?.data, 'error') ?? field(e.response?.data, 'code');
  if (status === 429) return code === 'sign_in_busy' ? 'busy' : 'tooManyTries';
  if (status === 401) return 'invalidCredentials';
  if (status === 423) return 'locked';
  if (status === 403) return 'noAccess';
  if (status >= 500) return 'serverDown';
  return 'generic';
}
