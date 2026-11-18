/** Pure session-reuse logic for the e2e helpers: no world/env imports, so the unit lane can load it in CI. */
export type TenantSession = { accessToken: string; refreshToken: string };

/** Seconds left before a JWT's `exp`; Infinity when it carries none or cannot be read. */
export function secondsUntilExpiry(token: string): number {
  try {
    const payload = JSON.parse(Buffer.from(token.split('.')[1].replace(/-/g, '+').replace(/_/g, '/'), 'base64').toString('utf8')) as { exp?: number };
    return typeof payload.exp === 'number' ? payload.exp - Date.now() / 1000 : Infinity;
  } catch {
    return Infinity;
  }
}

/** A persona's fresh session once its setup session has run out, so each persona signs in at most once per token life. */
const liveTenantSessions = new Map<string, TenantSession>();
export const TOKEN_MARGIN_SECONDS = 5 * 60;

/**
 * The session to use for a persona: the setup-time one while its access token has more than the margin left, else the
 * cached fresh one while IT does, else a new sign-in (`mint`), cached under `key`. Pure apart from the cache, so it is
 * unit-tested (unit/e2eSessionReuse.spec.ts).
 */
export async function liveSession(
  setup: TenantSession,
  key: string,
  mint: () => Promise<TenantSession>,
  cache: Map<string, TenantSession> = liveTenantSessions,
): Promise<TenantSession> {
  if (secondsUntilExpiry(setup.accessToken) > TOKEN_MARGIN_SECONDS) return setup;
  const cached = cache.get(key);
  if (cached && secondsUntilExpiry(cached.accessToken) > TOKEN_MARGIN_SECONDS) return cached;
  const fresh = await mint();
  cache.set(key, fresh);
  return fresh;
}

