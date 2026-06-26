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

const RELOAD_GUARD_KEY = 'chunk-reload-at';
// If we already force-reloaded within this window, don't do it again — show the
// error UI instead. Long enough to cover a fresh page load, short enough that a
// genuine later deploy still recovers automatically.
const RELOAD_GUARD_MS = 15_000;

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
    const last = Number(window.sessionStorage.getItem(RELOAD_GUARD_KEY) || 0);
    if (last && Date.now() - last < RELOAD_GUARD_MS) {
      // We just reloaded and still failed — stop, let the error UI show.
      return false;
    }
    window.sessionStorage.setItem(RELOAD_GUARD_KEY, String(Date.now()));
  } catch {
    // sessionStorage unavailable (private mode quirks) — reload anyway, once.
  }

  // Bypass the bfcache and any stale HTTP cache for the document.
  window.location.reload();
  return true;
}

/** Clear the guard once the app has mounted successfully on a fresh build. */
export function clearChunkReloadGuard(): void {
  if (typeof window === 'undefined') return;
  try {
    window.sessionStorage.removeItem(RELOAD_GUARD_KEY);
  } catch {
    /* no-op */
  }
}
