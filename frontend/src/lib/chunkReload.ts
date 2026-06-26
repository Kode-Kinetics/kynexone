/**
 * Deployment-skew recovery.
 *
 * When a new build is deployed (Vercel), any already-open tab still holds the
 * previous HTML, which references old content-hashed JS/CSS chunks. Those files
 * no longer exist on the new deployment, so the browser throws a ChunkLoadError
 * (or "Failed to fetch dynamically imported module"). Without handling, the app
 * renders blank / flickers.
 *
 * The cure is simple: a hard reload fetches the fresh HTML + new chunk names.
 * The danger is an infinite reload loop if the reload itself keeps failing, so
 * every reload is gated behind a short-lived sessionStorage timestamp.
 */

// Reload AT MOST ONCE per browser tab session. The flag is never auto-cleared,
// so even if a chunk error keeps firing after the reload, we never loop — we
// stop and let the error UI show. (A second genuine deploy in the same session
// is recovered by a manual refresh; that trade-off is worth never looping.)
const RELOAD_GUARD_KEY = 'chunk-reloaded-session';

const CHUNK_ERROR_PATTERNS = [
  /ChunkLoadError/i,
  /Loading chunk [\w-]+ failed/i,
  /Loading CSS chunk [\w-]+ failed/i,
  /Failed to fetch dynamically imported module/i,
  /error loading dynamically imported module/i,
  /Importing a module script failed/i,
  /'text\/html' is not a valid JavaScript MIME type/i,
];

export function isChunkLoadError(error: unknown): boolean {
  if (!error) return false;

  // Some bundlers set error.name === 'ChunkLoadError'
  const name = (error as { name?: unknown })?.name;
  if (typeof name === 'string' && /ChunkLoadError/i.test(name)) return true;

  const message =
    typeof error === 'string'
      ? error
      : ((error as { message?: unknown })?.message as string | undefined) ?? '';

  return CHUNK_ERROR_PATTERNS.some((re) => re.test(message));
}

/**
 * Force a one-time hard reload to recover from deployment skew.
 * Returns true if a reload was triggered, false if suppressed by the loop guard.
 */
export function attemptChunkReload(): boolean {
  if (typeof window === 'undefined') return false;

  try {
    if (window.sessionStorage.getItem(RELOAD_GUARD_KEY)) {
      // Already reloaded once this session and it still failed — STOP.
      // Looping is never acceptable; let the error UI show instead.
      return false;
    }
    window.sessionStorage.setItem(RELOAD_GUARD_KEY, '1');
  } catch {
    // sessionStorage unavailable (private mode quirks). Reloading blindly here
    // risks a loop, so do NOT reload — surface the error UI instead.
    return false;
  }

  // Bypass the bfcache and any stale HTTP cache for the document.
  window.location.reload();
  return true;
}
