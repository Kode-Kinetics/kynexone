/**
 * Words for a 429's Retry-After, matching the API's LoginAbuseGuard.WaitPhrase thresholds so the
 * page and the server's own message never disagree.
 */
export function waitPhrase(retryAfterHeader: unknown): string {
  const seconds = Number.parseInt(String(retryAfterHeader ?? ''), 10);
  if (!Number.isFinite(seconds) || seconds <= 10) return 'in a few seconds';
  if (seconds <= 90) return 'in about a minute';
  return 'in a few minutes';
}
